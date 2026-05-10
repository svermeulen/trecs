using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class WorldInfoTests
    {
        static readonly Tag UnusedTag = new(999999999, "TestUnused");

        #region GroupResolution

        [Test]
        public void WorldInfo_GetGroupsWithTags_ReturnsCorrect()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var wi = env.Accessor.WorldInfo;

            var groups = wi.GetGroupsWithTags(TestTags.Alpha);

            NAssert.AreEqual(1, groups.Count);
        }

        [Test]
        public void WorldInfo_GetSingleGroup_OneMatch_Returns()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TestTags.Alpha);

            NAssert.IsNotNull(group);
        }

        [Test]
        public void WorldInfo_GetSingleGroup_NoMatch_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            NAssert.Catch(() =>
            {
                wi.GetSingleGroupWithTags(UnusedTag);
            });
        }

        #endregion

        #region TemplateMapping

        [Test]
        public void WorldInfo_AllGroups_ExpectedCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var wi = env.Accessor.WorldInfo;

            // 1 global group + 1 alpha group + 1 beta group = 3
            NAssert.AreEqual(3, wi.AllGroups.Count);
        }

        [Test]
        public void WorldInfo_ResolvedTemplate_HasComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TestTags.Beta);
            var ct = wi.GetResolvedTemplateForGroup(group);

            NAssert.IsTrue(ct.HasComponent<TestInt>());
            NAssert.IsTrue(ct.HasComponent<TestFloat>());
        }

        [Test]
        public void WorldInfo_GroupHasComponent_True()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TestTags.Alpha);

            NAssert.IsTrue(wi.GroupHasComponent<TestInt>(group));
        }

        [Test]
        public void WorldInfo_GroupHasComponent_False()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TestTags.Alpha);

            NAssert.IsFalse(wi.GroupHasComponent<TestFloat>(group));
        }

        [Test]
        public void WorldInfo_GetGroupsWithTagsAndComponents_Correct()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var wi = env.Accessor.WorldInfo;

            // Beta has TestFloat, Alpha does not
            var groups = wi.GetGroupsWithTagsAndComponents<TestFloat>(TestTags.Beta);
            NAssert.AreEqual(1, groups.Count);

            // Alpha does not have TestFloat
            var groups2 = wi.GetGroupsWithTagsAndComponents<TestFloat>(TestTags.Alpha);
            NAssert.AreEqual(0, groups2.Count);
        }

        #endregion

        #region Globals

        [Test]
        public void WorldInfo_GlobalEntityIndex_Valid()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            var globalEntityIndex = wi.GlobalEntityIndex;

            // The global entityIndex should have a valid group
            NAssert.IsNotNull(globalEntityIndex);
        }

        [Test]
        public void WorldInfo_GlobalTemplate_Exists()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            NAssert.IsNotNull(wi.GlobalTemplate);
        }

        [Test]
        public void WorldInfo_GlobalGroups_HasOne()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            NAssert.AreEqual(1, wi.GlobalGroups.Count);
        }

        #endregion

        #region Partitions

        [Test]
        public void WorldInfo_WithPartitions_CorrectGroupCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var wi = env.Accessor.WorldInfo;

            // WithPartitions: Gamma+PartitionA, Gamma+PartitionB = 2 groups + 1 global = 3
            NAssert.AreEqual(3, wi.AllGroups.Count);

            var gammaGroups = wi.GetGroupsWithTags(TestTags.Gamma);
            NAssert.AreEqual(
                2,
                gammaGroups.Count,
                "Gamma tag should match both PartitionA and PartitionB groups"
            );
        }

        [Test]
        public void WorldInfo_GetGroupsWithTagsAndComponents_MultiComponent()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta,
                TestTemplates.WithPartitions
            );
            var wi = env.Accessor.WorldInfo;

            // TwoCompBeta has TestInt + TestFloat
            // SimpleAlpha has TestInt only
            // WithPartitions has TestInt + TestVec
            var groups = wi.GetGroupsWithTagsAndComponents<TestInt, TestFloat>(TestTags.Beta);
            NAssert.AreEqual(1, groups.Count, "Only Beta group has both TestInt and TestFloat");
        }

        [Test]
        public void WorldInfo_ResolvedTemplate_PartitionGroupsShareComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var wi = env.Accessor.WorldInfo;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            var groupA = wi.GetSingleGroupWithTags(partitionA);
            var groupB = wi.GetSingleGroupWithTags(partitionB);

            var templateA = wi.GetResolvedTemplateForGroup(groupA);
            var templateB = wi.GetResolvedTemplateForGroup(groupB);

            // Both partition groups should have the same components
            NAssert.IsTrue(templateA.HasComponent<TestInt>());
            NAssert.IsTrue(templateA.HasComponent<TestVec>());
            NAssert.IsTrue(templateB.HasComponent<TestInt>());
            NAssert.IsTrue(templateB.HasComponent<TestVec>());
        }

        #endregion

        #region Tags

        [Test]
        public void WorldInfo_GetGroupTags_MatchTemplate()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TestTags.Alpha);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(TestTags.Alpha));
        }

        #endregion
    }
}
