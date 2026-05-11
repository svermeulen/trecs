using System;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class StructuralChangeConflictTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        #region Managed Remove Supersedes Managed Swap

        [Test]
        public void ManagedSwapThenManagedRemove_EntityIsRemoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            a.SetTag<TestPartitionB>(entityIdx);
            a.RemoveEntity(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        [Test]
        public void ManagedRemoveThenManagedSwap_EntityIsRemoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            a.RemoveEntity(entityIdx);
            a.SetTag<TestPartitionB>(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        #endregion

        #region Native Remove Supersedes Native Swap

        [Test]
        public void NativeRemoveAndNativeSwap_EntityIsRemoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Native ops are processed removes-first regardless of queue order
            nativeEcs.SetTag<TestPartitionB>(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        #endregion

        #region Managed Swap + Native Remove (cross-path)

        [Test]
        public void ManagedSwapThenNativeRemove_EntityIsRemoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Managed swap queued first, then native remove
            a.SetTag<TestPartitionB>(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        #endregion

        #region Managed Remove + Native Swap (cross-path)

        [Test]
        public void ManagedRemoveThenNativeSwap_EntityIsRemoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Managed remove queued first, then native swap
            a.RemoveEntity(entityIdx);
            nativeEcs.SetTag<TestPartitionB>(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        #endregion

        #region Duplicate Removes

        [Test]
        public void ManagedDuplicateRemove_EntityRemovedOnce()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;

            a.AddEntity(PartitionA).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(PartitionA).Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Remove the same entity twice via managed path
            a.RemoveEntity(entityIdx);
            a.RemoveEntity(entityIdx);
            a.SubmitEntities();

            // Only one entity should be removed, leaving one
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
        }

        [Test]
        public void NativeDuplicateRemove_EntityRemovedOnce()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(PartitionA).Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Remove the same entity twice via native path (simulating two jobs)
            nativeEcs.RemoveEntity(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            // Only one entity should be removed, leaving one
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
        }

        [Test]
        public void ManagedAndNativeDuplicateRemove_EntityRemovedOnce()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity(PartitionA).Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Remove the same entity via both managed and native
            a.RemoveEntity(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            // Only one entity should be removed, leaving one
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
        }

        #endregion

        #region Other entities are preserved

        [Test]
        public void ManagedSwapThenNativeRemove_OtherEntitiesPreserved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Add 3 entities in PartitionA
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA).Set(new TestInt { Value = (i + 1) * 10 }).AssertComplete();
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);

            // Swap entity 0, then native-remove entity 0.
            // Entities 1 and 2 should be unaffected.
            var entityIdx = new EntityIndex(0, groupA);
            a.SetTag<TestPartitionB>(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        [Test]
        public void NativeDuplicateRemove_OtherEntitiesPreserved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Add 3 entities
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(PartitionA).Set(new TestInt { Value = (i + 1) * 10 }).AssertComplete();
            }
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Duplicate native remove on entity 0
            nativeEcs.RemoveEntity(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            // Only entity 0 removed, entities 1 and 2 survive
            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));
        }

        #endregion

        #region Multiple entities with mixed conflicts

        [Test]
        public void MultipleEntities_MixedConflicts_AllResolvedCorrectly()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Create 5 entities in PartitionA
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(PartitionA).Set(new TestInt { Value = (i + 1) * 10 }).AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(PartitionA));

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);

            // Entity 0: managed swap + native remove → removed
            a.SetTag<TestPartitionB>(new EntityIndex(0, groupA));
            nativeEcs.RemoveEntity(new EntityIndex(0, groupA));

            // Entity 1: managed remove + native swap → removed
            a.RemoveEntity(new EntityIndex(1, groupA));
            nativeEcs.SetTag<TestPartitionB>(new EntityIndex(1, groupA));

            // Entity 2: native duplicate remove → removed once
            nativeEcs.RemoveEntity(new EntityIndex(2, groupA));
            nativeEcs.RemoveEntity(new EntityIndex(2, groupA));

            // Entity 3: native swap only → moved to PartitionB
            nativeEcs.SetTag<TestPartitionB>(new EntityIndex(3, groupA));

            // Entity 4: no operations → stays in PartitionA
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));
        }

        [Test]
        public void NativeRemoveFromDifferentThreads_SwapFiltered()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            a.AddEntity(PartitionA).Set(new TestInt { Value = 10 }).AssertComplete();
            a.AddEntity(PartitionA).Set(new TestInt { Value = 20 }).AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            var entityIdx = new EntityIndex(0, groupA);

            // Thread 0 queues a swap, thread 1 queues a remove for the same entity
            nativeEcs.SetTag<TestPartitionB>(entityIdx);
            nativeEcs.RemoveEntity(entityIdx);
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        #endregion

        #region Native + Managed Mixed Operations

        [Test]
        public void NativeSwapManagedSwap_DifferentEntities_BothProcessed()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var refs = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                var init = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = (i + 1) * 10 })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // Entity 0: native swap to B
            // Entity 1: managed swap to B
            // Entity 2: stays
            nativeEcs.SetTag<TestPartitionB>(refs[0].ToIndex(a));
            a.SetTag<TestPartitionB>(refs[1].ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionB));

            // Both moved entities should have their data intact
            NAssert.AreEqual(10, a.Component<TestInt>(refs[0]).Read.Value);
            NAssert.AreEqual(20, a.Component<TestInt>(refs[1]).Read.Value);
            NAssert.AreEqual(30, a.Component<TestInt>(refs[2]).Read.Value);
        }

        [Test]
        public void NativeAddManagedRemove_SameSubmission_BothApplied()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Add 2 entities managed
            var refs = new EntityHandle[2];
            for (int i = 0; i < 2; i++)
            {
                var init = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete();
                refs[i] = init.Handle;
            }
            a.SubmitEntities();

            // In same submission: remove entity 0 (managed), add entity (native)
            a.RemoveEntity(refs[0]);
            var nativeInit = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0);
            nativeInit.Set(new TestInt { Value = 99 });
            a.SubmitEntities();

            // Should have 2 entities: entity 1 (value 1) and the native-added one (value 99)
            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsFalse(a.EntityExists(refs[0]));
            NAssert.IsTrue(a.EntityExists(refs[1]));
        }

        #endregion

        #region Component data integrity

        [Test]
        public void RemoveSupersededSwap_ComponentDataNotCorrupted()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Add entity in PartitionA with known value, plus a second entity
            a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec { X = 1.0f, Y = 2.0f })
                .AssertComplete();
            a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 200 })
                .Set(new TestVec { X = 3.0f, Y = 4.0f })
                .AssertComplete();
            a.SubmitEntities();

            var groupA = a.WorldInfo.GetSingleGroupWithTags(PartitionA);

            // Swap entity 0 then native-remove it. Entity 1 should survive with correct data.
            a.SetTag<TestPartitionB>(new EntityIndex(0, groupA));
            nativeEcs.RemoveEntity(new EntityIndex(0, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));

            // Verify the surviving entity has correct data
            var intComp = a.Query().WithTags(PartitionA).Single().Get<TestInt>();
            var vecComp = a.Query().WithTags(PartitionA).Single().Get<TestVec>();
            NAssert.AreEqual(200, intComp.Read.Value);
            NAssert.AreEqual(3.0f, vecComp.Read.X, 0.001f);
            NAssert.AreEqual(4.0f, vecComp.Read.Y, 0.001f);
        }

        #endregion
    }
}
