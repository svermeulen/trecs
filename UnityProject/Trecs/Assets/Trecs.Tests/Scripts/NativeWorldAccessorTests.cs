using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeWorldAccessorTests
    {
        #region AddEntity

        [Test]
        public void NativeAccessor_AddEntity_EntityExistsAfterSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1);
            init.Set(new TestInt { Value = 42 });
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
            var comp = a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>();
            NAssert.AreEqual(42, comp.Read.Value);
        }

        [Test]
        public void NativeAccessor_AddMultiple_AllExist()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            for (int i = 0; i < 5; i++)
            {
                nativeEcs
                    .AddEntity(TestTags.Alpha, sortKey: (uint)i)
                    .Set(new TestInt { Value = i * 10 });
            }
            a.SubmitEntities();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region RemoveEntity

        [Test]
        public void NativeAccessor_RemoveEntity_CountDecreases()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            nativeEcs.RemoveEntity(handles[1]);
            a.SubmitEntities();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsFalse(a.EntityExists(handles[1]));
        }

        [Test]
        public void NativeAccessor_RemoveByEntityIndex_Works()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            nativeEcs.RemoveEntity(handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region MoveTo

        [Test]
        public void NativeAccessor_MoveTo_ChangesGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            var handle = a.AddEntity(partitionA)
                .Set(new TestInt { Value = 77 })
                .Set(new TestVec())
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            nativeEcs.MoveTo(handle.ToIndex(a), partitionB);
            a.SubmitEntities();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(partitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(partitionB));
        }

        [Test]
        public void NativeAccessor_MoveTo_PreservesComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var partitionA = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var partitionB = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionB);

            var handle = a.AddEntity(partitionA)
                .Set(new TestInt { Value = 88 })
                .Set(new TestVec { X = 1.5f, Y = 2.5f })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            nativeEcs.MoveTo(handle.ToIndex(a), partitionB);
            a.SubmitEntities();

            var comp = a.Query().WithTags(partitionB).Single().Get<TestInt>();
            NAssert.AreEqual(88, comp.Read.Value);
        }

        #endregion

        #region Sort Key Determinism

        [Test]
        public void NativeAccessor_SortKeys_DeterministicOrder()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings { RequireDeterministicSubmission = true },
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            using var refs = a.ReserveEntityHandles(3, Allocator.Temp);

            // Add in reverse sort key order
            nativeEcs
                .AddEntity(TestTags.Alpha, sortKey: 30, refs[0])
                .Set(new TestInt { Value = 300 });
            nativeEcs
                .AddEntity(TestTags.Alpha, sortKey: 10, refs[1])
                .Set(new TestInt { Value = 100 });
            nativeEcs
                .AddEntity(TestTags.Alpha, sortKey: 20, refs[2])
                .Set(new TestInt { Value = 200 });
            a.SubmitEntities();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Alpha));

            // Entities should be ordered by sort key (10, 20, 30)
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.AreEqual(100, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
            NAssert.AreEqual(200, a.Component<TestInt>(new EntityIndex(1, group)).Read.Value);
            NAssert.AreEqual(300, a.Component<TestInt>(new EntityIndex(2, group)).Read.Value);
        }

        #endregion

        #region Mixed Native + Managed Operations

        [Test]
        public void NativeAccessor_MixedAddAndRemove_CorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Add 3 via managed API
            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(TestTags.Alpha)
                    .Set(new TestInt { Value = i })
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            // Add 2 via native, remove 1 via native
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1).Set(new TestInt { Value = 10 });
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 2).Set(new TestInt { Value = 20 });
            nativeEcs.RemoveEntity(handles[0]);
            a.SubmitEntities();

            // 3 - 1 + 2 = 4
            NAssert.AreEqual(4, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region Deferred Set Operations

        public struct NWATestSet : IEntitySet<QId1> { }

        [Test]
        public void NativeAccessor_SetAdd_EntityAppearsInSet()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSet<NWATestSet>(),
                QTestEntityA.Template
            );
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            a.Set<NWATestSet>().Defer.Add(new EntityIndex(0, group));
            a.SubmitEntities();

            var set = a.Set<NWATestSet>();
            NAssert.AreEqual(1, set.Read.Count);
            NAssert.IsTrue(set.Read.Exists(new EntityIndex(0, group)));
        }

        [Test]
        public void NativeAccessor_SetRemove_EntityRemovedFromSet()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSet<NWATestSet>(),
                QTestEntityA.Template
            );
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<NWATestSet>();
            set.Write.Add(new EntityIndex(0, group));
            a.SubmitEntities();
            NAssert.AreEqual(1, set.Read.Count);

            a.Set<NWATestSet>().Defer.Remove(new EntityIndex(0, group));
            a.SubmitEntities();

            NAssert.AreEqual(0, set.Read.Count);
        }

        #endregion
    }
}
