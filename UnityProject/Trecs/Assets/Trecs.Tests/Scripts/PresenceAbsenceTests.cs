using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Schema for presence/absence partition tests: a base tag + one binary dim
    // declared via the arity-1 IPartitionedBy<T> form. Entities live in either
    // {PaBase} (absent) or {PaBase, PaPresent} (present).
    public struct PaBase : ITag { }

    public struct PaPresent : ITag { }

    public partial class PaTestEntity : ITemplate, ITagged<PaBase>, IPartitionedBy<PaPresent>
    {
        TestInt TestInt;
    }

    [TestFixture]
    public class PresenceAbsenceTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(b => { }, PaTestEntity.Template);

        [Test]
        public void TwoPartitions_PresentAndAbsent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // The cross-product source-gen emits two partitions: absent ({PaBase})
            // and present ({PaBase, PaPresent}).
            NAssert.AreEqual(2, PaTestEntity.Template.Partitions.Count);
            NAssert.AreEqual(1, PaTestEntity.Template.Dimensions.Count);
        }

        [Test]
        public void AddEntity_DefaultsToAbsentPartition()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity<PaBase>().Set(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();

            int absentCount = a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count();
            int presentCount = a.Query().WithTags<PaBase, PaPresent>().Count();

            NAssert.AreEqual(1, absentCount);
            NAssert.AreEqual(0, presentCount);
        }

        [Test]
        public void AddEntity_WithPresentTag_LandsInPresentPartition()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity<PaBase, PaPresent>().Set(new TestInt { Value = 7 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<PaBase, PaPresent>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
        }

        [Test]
        public void AddTag_MovesAbsentEntityToPresent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<PaBase>().Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            a.AddTag<PaPresent>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<PaBase, PaPresent>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
        }

        [Test]
        public void RemoveTag_MovesPresentEntityToAbsent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<PaBase, PaPresent>()
                .Set(new TestInt { Value = 13 })
                .AssertComplete();
            a.SubmitEntities();

            a.RemoveTag<PaPresent>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Query().WithTags<PaBase, PaPresent>().Count());
            NAssert.AreEqual(1, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
        }

        [Test]
        public void RemoveTag_OnAlreadyAbsent_IsIdempotent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<PaBase>().Set(new TestInt { Value = 5 }).AssertComplete();
            a.SubmitEntities();

            // RemoveTag when already absent should be a no-op move (same destination group).
            a.RemoveTag<PaPresent>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<PaBase, PaPresent>().Count());
        }
    }
}
