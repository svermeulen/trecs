using NUnit.Framework;
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

            var seen = new System.Collections.Generic.HashSet<ushort>();
            foreach (var group in info.AllGroups)
            {
                NAssert.IsTrue(
                    group.Value < info.AllGroups.Count,
                    "GroupIndex {0} out of range [0, {1})",
                    group.Value,
                    info.AllGroups.Count
                );
                NAssert.IsTrue(seen.Add(group.Value), "Duplicate GroupIndex {0}", group.Value);
            }

            NAssert.AreEqual(info.AllGroups.Count, seen.Count);
        }

        [Test]
        public void GroupIndex_SizeIs2Bytes()
        {
            NAssert.AreEqual(
                2,
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<GroupIndex>()
            );
        }
    }
}
