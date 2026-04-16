using NUnit.Framework;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeComponentLookupTests
    {
        #region Read Lookup — Single Group

        [Test]
        public void ReadLookup_SingleGroup_ReadsCorrectValues()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .AssertComplete();
            }
            a.SubmitEntities();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            NAssert.IsTrue(lookup.IsCreated);

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.AreEqual(10, lookup[new EntityIndex(0, group)].Value);
            NAssert.AreEqual(20, lookup[new EntityIndex(1, group)].Value);
            NAssert.AreEqual(30, lookup[new EntityIndex(2, group)].Value);
        }

        #endregion

        #region Read Lookup — Multiple Groups

        [Test]
        public void ReadLookup_MultipleGroups_ReadsFromBoth()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var stateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
            var stateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

            a.AddEntity(stateA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec())
                .AssertComplete();
            a.AddEntity(stateB)
                .Set(new TestInt { Value = 200 })
                .Set(new TestVec())
                .AssertComplete();
            a.SubmitEntities();

            // Lookup by Gamma tag — matches both state groups
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TagSet.FromTags(TestTags.Gamma),
                Allocator.TempJob
            );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(stateA);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(stateB);

            NAssert.AreEqual(100, lookup[new EntityIndex(0, groupA)].Value);
            NAssert.AreEqual(200, lookup[new EntityIndex(0, groupB)].Value);
        }

        #endregion

        #region Write Lookup

        [Test]
        public void WriteLookup_ModifyComponent_PersistsValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            using var lookup = a.CreateNativeComponentLookupWrite<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            lookup[new EntityIndex(0, group)] = new TestInt { Value = 99 };

            // Verify via regular accessor
            NAssert.AreEqual(99, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
        }

        #endregion

        #region TryGet

        [Test]
        public void ReadLookup_TryGet_ReturnsTrueForMatchingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            bool found = lookup.TryGet(new EntityIndex(0, group), out var value);

            NAssert.IsTrue(found);
            NAssert.AreEqual(42, value.Value);
        }

        [Test]
        public void ReadLookup_TryGet_ReturnsFalseForMissingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(TestTags.Beta)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            // Lookup only covers Alpha group
            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var betaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            bool found = lookup.TryGet(new EntityIndex(0, betaGroup), out _);

            NAssert.IsFalse(found, "TryGet should return false for group not in lookup");
        }

        #endregion

        #region Exists

        [Test]
        public void ReadLookup_Exists_TrueForMatchingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.IsTrue(lookup.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void ReadLookup_Exists_FalseForMissingGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                TestTemplates.SimpleAlpha,
                TestTemplates.TwoCompBeta
            );
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(TestTags.Beta)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            var betaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);
            NAssert.IsFalse(lookup.Exists(new EntityIndex(0, betaGroup)));
        }

        #endregion

        #region Lookup After Structural Changes

        [Test]
        public void ReadLookup_AfterEntityRemoved_StillWorksForRemaining()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 10 })
                .AssertComplete()
                .Handle;
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 20 }).AssertComplete();
            a.SubmitEntities();

            // Remove first entity
            a.RemoveEntity(handle);
            a.SubmitEntities();

            using var lookup = a.CreateNativeComponentLookupRead<TestInt>(
                TestTags.Alpha,
                Allocator.TempJob
            );

            NAssert.IsTrue(lookup.IsCreated);
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            // After swap-back, entity at index 0 should have value 20
            NAssert.AreEqual(20, lookup[new EntityIndex(0, group)].Value);
        }

        #endregion
    }
}
