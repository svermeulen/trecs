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
                var tagSet = group.AsTagSet();
                var index = info.ToGroupIndex(tagSet);
                var roundTripped = info.ToTagSet(index);
                NAssert.AreEqual(tagSet, roundTripped);
            }
        }

        [Test]
        public void ToGroupIndex_ViaGroup_MatchesViaTagSet()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var info = env.Accessor.WorldInfo;

            foreach (var group in info.AllGroups)
            {
                var viaGroup = info.ToGroupIndex(group);
                var viaTagSet = info.ToGroupIndex(group.AsTagSet());
                NAssert.AreEqual(viaGroup, viaTagSet);
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
                var index = info.ToGroupIndex(group.AsTagSet());
                NAssert.IsTrue(
                    index.Value < info.AllGroups.Count,
                    "GroupIndex {0} out of range [0, {1})",
                    index.Value,
                    info.AllGroups.Count
                );
                NAssert.IsTrue(seen.Add(index.Value), "Duplicate GroupIndex {0}", index.Value);
            }

            NAssert.AreEqual(info.AllGroups.Count, seen.Count);
        }
    }
}
