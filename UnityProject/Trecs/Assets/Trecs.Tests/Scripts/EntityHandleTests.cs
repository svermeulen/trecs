using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class EntityHandleTests
    {
        #region Basics

        [Test]
        public void EntityHandle_Default_IsNull()
        {
            var entityHandle = default(EntityHandle);
            NAssert.IsTrue(entityHandle.IsNull);
        }

        [Test]
        public void EntityHandle_Created_IsNotNull()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;

            NAssert.IsFalse(entityHandle.IsNull);
        }

        [Test]
        public void EntityHandle_Equality_SameEntity()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle1 = init.Handle;
            var entityHandle2 = init.Handle;

            NAssert.IsTrue(entityHandle1.Equals(entityHandle2));
        }

        [Test]
        public void EntityHandle_Equality_DifferentEntity()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init1 = a.AddEntity(TestTags.Alpha).AssertComplete();
            var init2 = a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.IsFalse(init1.Handle.Equals(init2.Handle));
        }

        #endregion

        #region Conversion

        [Test]
        public void EntityHandle_ToIndex_Valid()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var entityIndex = entityHandle.ToIndex(a);
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            NAssert.AreEqual(group, entityIndex.GroupIndex);
        }

        [Test]
        public void EntityHandle_Exists_AfterSubmit_True()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(entityHandle));
        }

        [Test]
        public void EntityHandle_Exists_AfterRemove_False()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            a.RemoveEntity(entityHandle);
            a.SubmitEntities();

            NAssert.IsFalse(a.EntityExists(entityHandle));
        }

        #endregion

        #region Stability

        [Test]
        public void EntityHandle_StableAfterOtherAdded()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 11 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            // Add another entity
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(entityHandle));
            var comp = a.Component<TestInt>(entityHandle);
            NAssert.AreEqual(11, comp.Read.Value);
        }

        [Test]
        public void EntityHandle_StableAfterOtherRemoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init1 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 11 })
                .AssertComplete();
            var entityHandle1 = init1.Handle;

            var init2 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 22 })
                .AssertComplete();
            var entityHandle2 = init2.Handle;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 33 }).AssertComplete();
            a.SubmitEntities();

            // Remove the second entity
            a.RemoveEntity(entityHandle2);
            a.SubmitEntities();

            // First entity should still be valid
            NAssert.IsTrue(a.EntityExists(entityHandle1));
            var comp = a.Component<TestInt>(entityHandle1);
            NAssert.AreEqual(11, comp.Read.Value);
        }

        [Test]
        public void EntityHandle_StableAfterMove()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            var init = a.AddEntity(partitionA).Set(new TestInt { Value = 42 }).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var entityIndex = entityHandle.ToIndex(a);
            a.SetTag<TestPartitionB>(entityIndex);
            a.SubmitEntities();

            NAssert.IsTrue(a.EntityExists(entityHandle));
            var comp = a.Component<TestInt>(entityHandle);
            NAssert.AreEqual(42, comp.Read.Value);
        }

        [Test]
        public void EntityHandle_ToIndex_AfterOtherRemoved_StillValid()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            // Add three entities
            var init0 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 100 })
                .AssertComplete();

            var init1 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 200 })
                .AssertComplete();

            var init2 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 300 })
                .AssertComplete();

            a.SubmitEntities();

            var entityHandle0 = init0.Handle;
            var entityHandle2 = init2.Handle;

            // Remove the middle entity (triggers swap-back)
            a.RemoveEntity(init1.Handle);
            a.SubmitEntities();

            // Both remaining entity refs should still resolve to valid entity indices
            NAssert.IsTrue(a.EntityExists(entityHandle0));
            NAssert.IsTrue(a.EntityExists(entityHandle2));

            var comp0 = a.Component<TestInt>(entityHandle0);
            var comp2 = a.Component<TestInt>(entityHandle2);
            NAssert.AreEqual(100, comp0.Read.Value);
            NAssert.AreEqual(300, comp2.Read.Value);
        }

        [Test]
        public void EntityHandle_ToIndex_AfterMultipleStructuralCycles()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            // Create tracked entity
            var init = a.AddEntity(partitionA)
                .Set(new TestInt { Value = 77 })
                .Set(new TestVec { X = 1.0f, Y = 2.0f })
                .AssertComplete();
            var trackedRef = init.Handle;
            a.SubmitEntities();

            // Round 1: add 5 entities, remove 2
            var roundRefs = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                var r = a.AddEntity(partitionA).Set(new TestInt { Value = i }).AssertComplete();
                roundRefs[i] = r.Handle;
            }
            a.SubmitEntities();
            a.RemoveEntity(roundRefs[0]);
            a.RemoveEntity(roundRefs[2]);
            a.SubmitEntities();

            // Round 2: move tracked entity to PartitionB, add 3 more, remove 1
            a.SetTag<TestPartitionB>(trackedRef.ToIndex(a));
            var round2Refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var r = a.AddEntity(partitionA)
                    .Set(new TestInt { Value = 100 + i })
                    .AssertComplete();
                round2Refs[i] = r.Handle;
            }
            a.SubmitEntities();
            a.RemoveEntity(round2Refs[1]);
            a.SubmitEntities();

            // Round 3: add 5 more to PartitionA
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(partitionA).Set(new TestInt { Value = 200 + i }).AssertComplete();
            }
            a.SubmitEntities();

            // Verify tracked entity ref still resolves correctly
            NAssert.IsTrue(a.EntityExists(trackedRef), "Tracked entity should still exist");
            var entityIndex = trackedRef.ToIndex(a);
            var intComp = a.Component<TestInt>(entityIndex);
            var vecComp = a.Component<TestVec>(entityIndex);
            NAssert.AreEqual(77, intComp.Read.Value);
            NAssert.AreEqual(1.0f, vecComp.Read.X, 0.001f);
            NAssert.AreEqual(2.0f, vecComp.Read.Y, 0.001f);
        }

        [Test]
        public void EntityHandle_MultipleConversions_Consistent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;
            a.SubmitEntities();

            var entityIndex1 = entityHandle.ToIndex(a);
            var entityIndex2 = entityHandle.ToIndex(a);

            NAssert.AreEqual(entityIndex1, entityIndex2);
        }

        #endregion

        #region Hash Determinism

        [Test]
        public void EntityHandle_GetStableHashCode_SameForSameRef()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init = a.AddEntity(TestTags.Alpha).AssertComplete();
            var entityHandle = init.Handle;

            NAssert.AreEqual(entityHandle.GetStableHashCode(), entityHandle.GetStableHashCode());
        }

        [Test]
        public void EntityHandle_GetStableHashCode_DifferentForDifferentRefs()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var init1 = a.AddEntity(TestTags.Alpha).AssertComplete();
            var init2 = a.AddEntity(TestTags.Alpha).AssertComplete();

            NAssert.AreNotEqual(init1.Handle.GetStableHashCode(), init2.Handle.GetStableHashCode());
        }

        [Test]
        public void EntityHandle_GetStableHashCode_DeterministicAcrossWorlds()
        {
            int HashForFirstEntity()
            {
                using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
                var init = env.Accessor.AddEntity(TestTags.Alpha).AssertComplete();
                return init.Handle.GetStableHashCode();
            }

            var hash1 = HashForFirstEntity();
            var hash2 = HashForFirstEntity();

            NAssert.AreEqual(
                hash1,
                hash2,
                "First entity ref hash should be deterministic across world instances"
            );
        }

        #endregion
    }
}
