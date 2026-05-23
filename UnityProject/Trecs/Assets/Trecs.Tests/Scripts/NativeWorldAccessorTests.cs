using NUnit.Framework;
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

            using var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var init = nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1, refs[0]);
            init.Set(new TestInt { Value = 42 });
            a.Submit();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
            var comp = a.Query().WithTags(TestTags.Alpha).SingleHandle().Component<TestInt>(a);
            NAssert.AreEqual(42, comp.Read.Value);
        }

        [Test]
        public void NativeAccessor_AddMultiple_AllExist()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            using var refs = a.ReserveEntityHandles(5, Allocator.Temp);
            for (int i = 0; i < 5; i++)
            {
                nativeEcs
                    .AddEntity(TestTags.Alpha, sortKey: (uint)i, refs[i])
                    .Set(new TestInt { Value = i * 10 });
            }
            a.Submit();

            NAssert.AreEqual(5, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        #endregion

        #region AddEntity (void / handleless)

        [Test]
        public void NativeAccessor_AddEntity_Void_EntityExistsAfterSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // No ReserveEntityHandles needed for the fire-and-forget variant.
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1).Set(new TestInt { Value = 42 });
            a.Submit();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
            var comp = a.Query().WithTags(TestTags.Alpha).SingleHandle().Component<TestInt>(a);
            NAssert.AreEqual(42, comp.Read.Value);
        }

        [Test]
        public void NativeAccessor_AddEntity_Void_GenericTagOverloads_Work()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            nativeEcs.AddEntity<TestAlpha>(sortKey: 1).Set(new TestInt { Value = 1 });
            nativeEcs.AddEntity<TestAlpha>(sortKey: 2).Set(new TestInt { Value = 2 });
            a.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void NativeAccessor_AddEntity_Void_SortKeyOrderRespected()
        {
            // Same shape as the pre-reserved sort-key test, but using the
            // void overloads — the post-sort id claim must walk sorted order
            // so deterministic ordering still holds.
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings(),
                TestTemplates.SimpleAlpha
            );
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // Add in reverse sort key order
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 30).Set(new TestInt { Value = 300 });
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 10).Set(new TestInt { Value = 100 });
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 20).Set(new TestInt { Value = 200 });
            a.Submit();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Alpha));

            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            NAssert.AreEqual(100, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
            NAssert.AreEqual(200, a.Component<TestInt>(new EntityIndex(1, group)).Read.Value);
            NAssert.AreEqual(300, a.Component<TestInt>(new EntityIndex(2, group)).Read.Value);
        }

        [Test]
        public void NativeAccessor_AddEntity_Void_MixedWithPreReserved_BothLand()
        {
            // Pre-reserved and void overloads should compose freely in the
            // same submission — the void slot gets its id post-sort while
            // the pre-reserved slot keeps its caller-assigned id.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            using var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            nativeEcs
                .AddEntity(TestTags.Alpha, sortKey: 1, refs[0])
                .Set(new TestInt { Value = 11 });
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 2).Set(new TestInt { Value = 22 });
            a.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            // Pre-reserved handle must resolve to its entity.
            NAssert.IsTrue(refs[0].Exists(a));
            NAssert.AreEqual(11, refs[0].Component<TestInt>(a).Read.Value);
        }

        [Test]
        public void NativeAccessor_AddEntity_Void_VoidAddedEntityIsRemovableByTag()
        {
            // Without a handle, the canonical way to clean up a fire-and-forget
            // entity is by tag-query → EntityIndex → Remove. Make sure that
            // composition works.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1).Set(new TestInt { Value = 99 });
            a.Submit();

            var handle = a.Query().WithTags(TestTags.Alpha).SingleHandle();
            handle.Remove(a);
            a.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
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
            a.Submit();

            nativeEcs.RemoveEntity(handles[1]);
            a.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.IsFalse(handles[1].Exists(a));
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
            a.Submit();

            nativeEcs.RemoveEntity(handle.ToIndex(a));
            a.Submit();

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
            a.Submit();

            nativeEcs.SetTag<TestPartitionB>(handle.ToIndex(a));
            a.Submit();

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
            a.Submit();

            nativeEcs.SetTag<TestPartitionB>(handle.ToIndex(a));
            a.Submit();

            var comp = a.Query().WithTags(partitionB).SingleHandle().Component<TestInt>(a);
            NAssert.AreEqual(88, comp.Read.Value);
        }

        #endregion

        #region Sort Key Determinism

        [Test]
        public void NativeAccessor_SortKeys_DeterministicOrder()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings(),
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
            a.Submit();

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
            a.Submit();

            // Add 2 via native, remove 1 via native
            using var newRefs = a.ReserveEntityHandles(2, Allocator.Temp);
            nativeEcs
                .AddEntity(TestTags.Alpha, sortKey: 1, newRefs[0])
                .Set(new TestInt { Value = 10 });
            nativeEcs
                .AddEntity(TestTags.Alpha, sortKey: 2, newRefs[1])
                .Set(new TestInt { Value = 20 });
            nativeEcs.RemoveEntity(handles[0]);
            a.Submit();

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
            a.Submit();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            a.Set<NWATestSet>().DeferredAdd(new EntityIndex(0, group));
            a.Submit();

            var set = a.Set<NWATestSet>();
            NAssert.AreEqual(1, set.Read.Count);
            NAssert.IsTrue(set.Read.Contains(new EntityIndex(0, group)));
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
            a.Submit();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var set = a.Set<NWATestSet>();
            set.Write.Add(new EntityIndex(0, group));
            a.Submit();
            NAssert.AreEqual(1, set.Read.Count);

            a.Set<NWATestSet>().DeferredRemove(new EntityIndex(0, group));
            a.Submit();

            NAssert.AreEqual(0, set.Read.Count);
        }

        #endregion
    }
}
