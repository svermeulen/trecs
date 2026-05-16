using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Regression coverage for the ComponentArray&lt;T&gt; _count / _values.Length
    /// split. The OnRemoved callback path depends on removed entities living
    /// past the logical entity count but inside the backing buffer, so the
    /// [ForEachEntity]-generated range overload can read their component data
    /// via the same NativeBuffer view. Collapsing _count into _values.Length
    /// (commit 65fe89797b, reverted in 257784a0a3) silently broke this — the
    /// post-remove ResizeUninitialized shrank the buffer view's length and the
    /// generated `values1[__i]` access threw IndexOutOfRangeException on the
    /// very first iteration. The bug only manifested in PlayMode sample scenes
    /// (FeedingFrenzy / RemoveCleanupHandler.OnFishRemoved) because no
    /// EditMode test exercised this codepath.
    /// </summary>
    [TestFixture]
    public partial class ForEachOnRemovedBufferAccessTests
    {
        readonly List<int> _observedValues = new();

        // [ForEachEntity] generates a sibling overload that takes
        // (GroupIndex, EntityRange, WorldAccessor) and reads each entity's
        // TestInt via the buffer view — i.e. exactly the codepath that
        // regressed.
        [ForEachEntity]
        void OnAlphaRemoved(in TestInt value)
        {
            _observedValues.Add(value.Value);
        }

        [Test]
        public void OnRemoved_ForEachEntity_ReadsComponentDuringWholeGroupRemoval()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 1; i <= 3; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            _observedValues.Clear();

            var sub = a.Events.EntitiesWithTags(TestTags.Alpha).OnRemoved(OnAlphaRemoved);

            // Whole-group removal drives the worst case: newCount == 0, which
            // is what produced "index 0 - length 0" in the original regression.
            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(3, _observedValues.Count);
            _observedValues.Sort();
            NAssert.AreEqual(1, _observedValues[0]);
            NAssert.AreEqual(2, _observedValues[1]);
            NAssert.AreEqual(3, _observedValues[2]);

            sub.Dispose();
        }

        [Test]
        public void OnRemoved_ForEachEntity_ReadsComponentDuringPartialGroupRemoval()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 1; i <= 5; i++)
            {
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            _observedValues.Clear();

            var sub = a.Events.EntitiesWithTags(TestTags.Alpha).OnRemoved(OnAlphaRemoved);

            // Remove a subset (not the whole group) so the post-remove count
            // is non-zero — exercises the [newCount, originalCount) buffer
            // window in the case where the bounds-check would have rejected
            // the upper-half indices specifically.
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, group));
            a.RemoveEntity(new EntityIndex(2, group));
            a.SubmitEntities();

            NAssert.AreEqual(2, _observedValues.Count);
        }
    }
}
