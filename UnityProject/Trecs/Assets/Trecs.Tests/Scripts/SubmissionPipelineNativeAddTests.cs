using NUnit.Framework;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for native add operations interacting with other structural changes
    /// in the submission pipeline.
    /// </summary>
    [TestFixture]
    public class SubmissionPipelineNativeAddTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
        static readonly TagSet PartitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

        TestEnvironment CreateEnv(bool deterministic = false)
        {
            var settings = new WorldSettings { RequireDeterministicSubmission = deterministic };
            return EcsTestHelper.CreateEnvironment(settings, TestTemplates.WithPartitions);
        }

        #region Native add + native remove in same frame

        [Test]
        public void NativeAdd_PlusNativeRemoveExisting_BothApplied()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var existing = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Native add new entity + native remove existing
            var init = nativeEcs.AddEntity(PartitionA, sortKey: 0);
            init.Set(new TestInt { Value = 99 });
            init.Set(new TestVec());
            nativeEcs.RemoveEntity(existing.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.IsFalse(a.EntityExists(existing));
        }

        [Test]
        public void NativeAdd_PlusNativeRemoveMultiple_CountCorrect()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Remove 3 existing, add 2 new (native)
            nativeEcs.RemoveEntity(handles[0].ToIndex(a));
            nativeEcs.RemoveEntity(handles[2].ToIndex(a));
            nativeEcs.RemoveEntity(handles[4].ToIndex(a));

            for (int i = 0; i < 2; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)i);
                init.Set(new TestInt { Value = 100 + i });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            // 5 - 3 + 2 = 4
            NAssert.AreEqual(4, a.CountEntitiesWithTags(PartitionA));
            NAssert.IsFalse(a.EntityExists(handles[0]));
            NAssert.IsTrue(a.EntityExists(handles[1]));
            NAssert.IsFalse(a.EntityExists(handles[2]));
            NAssert.IsTrue(a.EntityExists(handles[3]));
            NAssert.IsFalse(a.EntityExists(handles[4]));
        }

        #endregion

        #region Native add + move in same frame

        [Test]
        public void NativeAdd_PlusMoveExisting_BothApplied()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 10 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Move existing to PartitionB + native add new to PartitionA
            a.MoveTo(handle.ToIndex(a), PartitionB);
            var init = nativeEcs.AddEntity(PartitionA, sortKey: 0);
            init.Set(new TestInt { Value = 77 });
            init.Set(new TestVec());
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));
            NAssert.AreEqual(10, a.Component<TestInt>(handle).Read.Value);
        }

        [Test]
        public void NativeAdd_PlusMoveAndRemove_AllApplied()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Move 0 to PartitionB, remove 1, native add 2 new
            a.MoveTo(handles[0].ToIndex(a), PartitionB);
            a.RemoveEntity(handles[1]);
            for (int i = 0; i < 2; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)i);
                init.Set(new TestInt { Value = 100 + i });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            // PartitionA: 2, 3 (original) + 100, 101 (new) = 4
            // PartitionB: 0 (moved) = 1
            NAssert.AreEqual(4, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));
            NAssert.AreEqual(0, a.Component<TestInt>(handles[0]).Read.Value);
            NAssert.IsFalse(a.EntityExists(handles[1]));
        }

        #endregion

        #region Deterministic ordering with reserved handles

        [Test]
        public void DeterministicAdd_ReverseSortKeys_OrderedCorrectly()
        {
            using var env = CreateEnv(deterministic: true);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            const int count = 10;
            var refs = a.ReserveEntityHandles(count, Allocator.Temp);

            // Add in reverse order with sort keys 9,8,...,0
            for (int i = 0; i < count; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)(count - 1 - i), refs[i]);
                init.Set(new TestInt { Value = count - 1 - i });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            refs.Dispose();

            // Entities should be ordered by sort key (ascending)
            var group = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            for (int i = 0; i < count; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(
                    i,
                    comp.Read.Value,
                    $"Entity at index {i} should have value {i} (deterministic sort)"
                );
            }
        }

        [Test]
        public void DeterministicAdd_MixedManagedAndNative_ManagedFirst()
        {
            // Managed adds should appear before native adds in the final layout
            using var env = CreateEnv(deterministic: true);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Managed add
            var managedHandle = a.AddEntity(PartitionA)
                .Set(new TestInt { Value = 1000 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;

            // Native adds
            var refs = a.ReserveEntityHandles(3, Allocator.Temp);
            for (int i = 0; i < 3; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)i, refs[i]);
                init.Set(new TestInt { Value = i });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            refs.Dispose();

            // Managed entity should be at index 0
            var group = a.WorldInfo.GetSingleGroupWithTags(PartitionA);
            NAssert.AreEqual(
                1000,
                a.Component<TestInt>(new EntityIndex(0, group)).Read.Value,
                "Managed add should be at index 0"
            );

            // Native adds should follow in sort-key order
            for (int i = 0; i < 3; i++)
            {
                NAssert.AreEqual(
                    i,
                    a.Component<TestInt>(new EntityIndex(1 + i, group)).Read.Value,
                    $"Native add with sort key {i} should be at index {1 + i}"
                );
            }
        }

        [Test]
        public void DeterministicAdd_PlusRemoveExisting_OrderStillCorrect()
        {
            using var env = CreateEnv(deterministic: true);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Pre-existing entities
            var existing = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                existing[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = -(i + 1) })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Remove entity 1, native add 5 new with sort keys
            a.RemoveEntity(existing[1]);
            var refs = a.ReserveEntityHandles(5, Allocator.Temp);
            for (int i = 0; i < 5; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)i, refs[i]);
                init.Set(new TestInt { Value = i * 10 });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            refs.Dispose();

            // Should have 2 original + 5 new = 7
            NAssert.AreEqual(7, a.CountEntitiesWithTags(PartitionA));
            NAssert.IsFalse(a.EntityExists(existing[1]));
            NAssert.IsTrue(a.EntityExists(existing[0]));
            NAssert.IsTrue(a.EntityExists(existing[2]));
        }

        #endregion

        #region Large-scale native adds

        [Test]
        public void LargeScale_NativeAdds_AllExistWithCorrectData()
        {
            using var env = CreateEnv(deterministic: true);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            const int count = 200;
            var refs = a.ReserveEntityHandles(count, Allocator.Temp);

            for (int i = 0; i < count; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)i, refs[i]);
                init.Set(new TestInt { Value = i });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            NAssert.AreEqual(count, a.CountEntitiesWithTags(PartitionA));

            // Verify all entities accessible via reserved handles
            for (int i = 0; i < count; i++)
            {
                NAssert.IsTrue(a.EntityExists(refs[i]), $"Entity {i} should exist");
                NAssert.AreEqual(
                    i,
                    a.Component<TestInt>(refs[i]).Read.Value,
                    $"Entity {i} data should be intact"
                );
            }

            refs.Dispose();
        }

        [Test]
        public void LargeScale_NativeAddsAndRemoves_Mixed()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // First add 100 entities
            var handles = new EntityHandle[100];
            for (int i = 0; i < 100; i++)
            {
                handles[i] = a.AddEntity(PartitionA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Remove every 3rd, native add 50 new
            for (int i = 0; i < 100; i += 3)
                nativeEcs.RemoveEntity(handles[i].ToIndex(a));
            for (int i = 0; i < 50; i++)
            {
                var init = nativeEcs.AddEntity(PartitionA, sortKey: (uint)i);
                init.Set(new TestInt { Value = 1000 + i });
                init.Set(new TestVec());
            }
            a.SubmitEntities();

            int removedCount = (100 + 2) / 3; // 34
            NAssert.AreEqual(100 - removedCount + 50, a.CountEntitiesWithTags(PartitionA));
        }

        #endregion
    }
}
