using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class GroupIndexRegistryTests
    {
        [Test]
        public void ToGroupIndex_ToTagSet_RoundTrips()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var info = env.Accessor.WorldInfo;

            foreach (var group in info.AllGroups)
            {
                var tagSet = info.ToTagSet(group);
                var index = info.ToGroupIndex(tagSet);
                var roundTripped = info.ToTagSet(index);
                NAssert.AreEqual(tagSet, roundTripped);
                NAssert.AreEqual(group, index);
            }
        }

        [Test]
        public void GroupIndices_AreSequentialFromZero()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var info = env.Accessor.WorldInfo;

            var seen = new HashSet<int>();
            foreach (var group in info.AllGroups)
            {
                NAssert.IsFalse(group.IsNull, "Real groups must never be Null");
                NAssert.IsTrue(
                    group.Index < info.AllGroups.Count,
                    "GroupIndex {0} out of range [0, {1})",
                    group.Index,
                    info.AllGroups.Count
                );
                NAssert.IsTrue(seen.Add(group.Index), "Duplicate GroupIndex {0}", group.Index);
            }

            NAssert.AreEqual(info.AllGroups.Count, seen.Count);
        }

        [Test]
        public void GroupIndex_SizeIs2Bytes()
        {
            NAssert.AreEqual(2, UnsafeUtility.SizeOf<GroupIndex>());
        }

        [Test]
        public void GroupIndex_Default_IsNull()
        {
            GroupIndex g = default;
            NAssert.IsTrue(g.IsNull);
            NAssert.AreEqual(GroupIndex.Null, g);
        }

        [Test]
        public void GroupIndex_Index_ThrowsOnNull()
        {
            GroupIndex g = default;
            NAssert.Throws<TrecsException>(() =>
            {
                var _ = g.Index;
            });
        }

        [Test]
        public void EntityIndex_Default_IsNull()
        {
            EntityIndex e = default;
            NAssert.IsTrue(e.IsNull);
            NAssert.AreEqual(EntityIndex.Null, e);
        }
    }
}
