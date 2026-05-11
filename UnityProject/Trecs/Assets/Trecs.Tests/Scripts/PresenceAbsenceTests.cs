using NUnit.Framework;
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
        public void SetTag_MovesAbsentEntityToPresent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<PaBase>().Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            a.SetTag<PaPresent>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<PaBase, PaPresent>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
        }

        [Test]
        public void UnsetTag_MovesPresentEntityToAbsent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<PaBase, PaPresent>()
                .Set(new TestInt { Value = 13 })
                .AssertComplete();
            a.SubmitEntities();

            a.UnsetTag<PaPresent>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.Query().WithTags<PaBase, PaPresent>().Count());
            NAssert.AreEqual(1, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
        }

        [Test]
        public void UnsetTag_OnAlreadyAbsent_IsIdempotent()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<PaBase>().Set(new TestInt { Value = 5 }).AssertComplete();
            a.SubmitEntities();

            // UnsetTag when already absent should be a no-op move (same destination group).
            a.UnsetTag<PaPresent>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<PaBase>().WithoutTags<PaPresent>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<PaBase, PaPresent>().Count());
        }
    }

    // Multi-dim cross-product test: one presence/absence dim + one binary-variant
    // dim. Yields 4 partitions; SetTag/UnsetTag on either dim preserves the
    // other.
    public struct McBase : ITag { }

    public struct McPoisoned : ITag { } // presence/absence dim

    public struct McAlive : ITag { } // variant dim

    public struct McDead : ITag { } // variant dim

    public partial class McTestEntity
        : ITemplate,
            ITagged<McBase>,
            IPartitionedBy<McPoisoned>,
            IPartitionedBy<McAlive, McDead>
    {
        TestInt TestInt;
    }

    [TestFixture]
    public class MultiDimPartitionTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);

        [Test]
        public void CrossProduct_EmitsFourPartitions()
        {
            // 1 presence/absence dim (2 partitions) × 1 binary-variant dim (2 partitions) = 4.
            NAssert.AreEqual(4, McTestEntity.Template.Partitions.Count);
            NAssert.AreEqual(2, McTestEntity.Template.Dimensions.Count);
        }

        [Test]
        public void UnsetTag_PreservesOtherDimension()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Start as poisoned + alive.
            var init = a.AddEntity<McBase, McPoisoned, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // UnsetTag<Poisoned> should toggle just the poisoned dim, keeping McAlive.
            a.UnsetTag<McPoisoned>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<McBase, McAlive>().WithoutTags<McPoisoned>().Count()
            );
            NAssert.AreEqual(0, a.Query().WithTags<McBase, McAlive, McPoisoned>().Count());
        }

        [Test]
        public void SetTag_VariantDim_SwapsSiblingNotPoisoning()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Start as poisoned + alive.
            var init = a.AddEntity<McBase, McPoisoned, McAlive>()
                .Set(new TestInt { Value = 2 })
                .AssertComplete();
            a.SubmitEntities();

            // SetTag<Dead> should swap the Alive/Dead variant; Poisoned stays.
            a.SetTag<McDead>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<McBase, McDead, McPoisoned>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<McBase, McAlive>().Count());
        }

        [Test]
        public void Warmup_DoesNotThrow_AndEntitiesCanStillBeAdded()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Warm a specific group, then verify the world still works
            // normally for adds/queries into that group.
            a.Warmup<McBase, McAlive>(initialCapacity: 16);

            a.AddEntity<McBase, McAlive>().Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity<McBase, McAlive>().Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(2, a.Query().WithTags<McBase, McAlive>().Count());
        }

        [Test]
        public void WarmupAllGroups_DoesNotThrow()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Restoring the pre-0.x eager-allocation behavior must remain a
            // one-call escape hatch and round-trip cleanly through to the
            // submission pipeline.
            a.WarmupAllGroups(initialCapacityPerGroup: 4);

            // Sanity: adds into a previously-warmed group still work and
            // queries return the right counts.
            a.AddEntity<McBase, McAlive>().Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(1, a.Query().WithTags<McBase, McAlive>().Count());
        }

        [Test]
        public void TwoSetTagsOnIndependentDims_CoalesceIntoOneMove()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Start as alive, not poisoned.
            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 3 })
                .AssertComplete();
            a.SubmitEntities();

            // Two SetTag calls on independent dims (poisoned dim + alive/dead dim).
            // They should merge into a single move with the entity landing in
            // {McBase, McDead, McPoisoned}.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<McDead>(idx);
            a.SetTag<McPoisoned>(idx);
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<McBase, McDead, McPoisoned>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<McBase, McAlive>().Count());
        }

        [Test]
        public void TwoSetTagsOnSameDim_Throws()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 4 })
                .AssertComplete();
            a.SubmitEntities();

            // Both SetTag<McAlive> and SetTag<McDead> target the same dim
            // (Alive/Dead). Coalescing detects the conflict at submission and
            // throws — there is no "first wins" or "last wins" knob.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<McAlive>(idx);
            a.SetTag<McDead>(idx);
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void SetTagThenUnsetTagOnSamePresenceDim_Throws()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 5 })
                .AssertComplete();
            a.SubmitEntities();

            // SetTag<McPoisoned> and UnsetTag<McPoisoned> are also a same-dim
            // conflict, even though they're different verbs — the framework
            // treats any pair of ops touching the same dim as a conflict.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<McPoisoned>(idx);
            a.UnsetTag<McPoisoned>(idx);
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }
    }
}
