using NUnit.Framework;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public partial class AddEntityTests
    {
        [Test]
        public unsafe void AddEntity_WritesHeaderAndComponent_ToBagSlot()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;
            var wi = a.WorldInfo;
            var betaGroup = wi.GetSingleGroupWithTags(TestTags.Beta);
            var bags = env.Accessor.World.EntitySubmitter.PerGroupAddBags;
            int slotSize = bags.SlotSize(betaGroup.Index);

            var refs = a.ReserveEntityHandles(2, Allocator.Temp);
            var nativeEcs = a.ToNative();

            nativeEcs
                .AddEntity(TestTags.Beta, sortKey: 100, refs[0])
                .Set(new TestInt { Value = 42 });
            nativeEcs
                .AddEntity(TestTags.Beta, sortKey: 200, refs[1])
                .Set(new TestFloat { Value = 3.5f });

            var cell = bags.GetCell(0, betaGroup.Index);
            NAssert.AreEqual(slotSize * 2, cell.Length);

            // First slot: TestInt set, TestFloat unset.
            var hdr0 = (FastAddSlotHeader*)cell.Ptr;
            NAssert.AreEqual(refs[0], hdr0->ReservedRef);
            NAssert.AreEqual(100u, hdr0->SortKey);

            // Find which component slot index TestInt occupies in this template's layout
            // (Order is template-defined, not call-order).
            var layoutHeader = wi.ComponentLayouts.Headers[betaGroup.Index];
            int testIntSlotIndex = -1;
            int testFloatSlotIndex = -1;
            for (int i = 0; i < layoutHeader.ComponentCount; i++)
            {
                var entry = wi.ComponentLayouts.Entries[layoutHeader.FirstComponentIndex + i];
                if (entry.TypeIdValue == TypeId<TestInt>.Value.Value)
                    testIntSlotIndex = i;
                if (entry.TypeIdValue == TypeId<TestFloat>.Value.Value)
                    testFloatSlotIndex = i;
            }
            NAssert.GreaterOrEqual(testIntSlotIndex, 0);
            NAssert.GreaterOrEqual(testFloatSlotIndex, 0);

            NAssert.IsTrue(hdr0->SetMask.IsSet(testIntSlotIndex));
            NAssert.IsFalse(hdr0->SetMask.IsSet(testFloatSlotIndex));

            // Component bytes follow header; TestInt occupies its offset.
            byte* compBytes0 = (byte*)hdr0 + sizeof(FastAddSlotHeader);
            int testIntOffset = wi.ComponentLayouts
                .Entries[layoutHeader.FirstComponentIndex + testIntSlotIndex]
                .ByteOffset;
            int writtenInt = *(int*)(compBytes0 + testIntOffset);
            NAssert.AreEqual(42, writtenInt);

            // Second slot: TestFloat set, TestInt unset.
            var hdr1 = (FastAddSlotHeader*)((byte*)hdr0 + slotSize);
            NAssert.AreEqual(refs[1], hdr1->ReservedRef);
            NAssert.AreEqual(200u, hdr1->SortKey);
            NAssert.IsTrue(hdr1->SetMask.IsSet(testFloatSlotIndex));
            NAssert.IsFalse(hdr1->SetMask.IsSet(testIntSlotIndex));

            byte* compBytes1 = (byte*)hdr1 + sizeof(FastAddSlotHeader);
            int testFloatOffset = wi.ComponentLayouts
                .Entries[layoutHeader.FirstComponentIndex + testFloatSlotIndex]
                .ByteOffset;
            float writtenFloat = *(float*)(compBytes1 + testFloatOffset);
            NAssert.AreEqual(3.5f, writtenFloat);

            refs.Dispose();
        }

        [Test]
        public unsafe void AddEntity_SetMultipleComponents_BothBitsSet()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;
            var wi = a.WorldInfo;
            var betaGroup = wi.GetSingleGroupWithTags(TestTags.Beta);
            var bags = env.Accessor.World.EntitySubmitter.PerGroupAddBags;

            var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var nativeEcs = a.ToNative();

            nativeEcs
                .AddEntity(TestTags.Beta, sortKey: 0, refs[0])
                .Set(new TestInt { Value = 7 })
                .Set(new TestFloat { Value = 1.5f });

            var hdr = (FastAddSlotHeader*)bags.GetCell(0, betaGroup.Index).Ptr;
            NAssert.IsTrue(hdr->SetMask.IsSet(0));
            NAssert.IsTrue(hdr->SetMask.IsSet(1));
            NAssert.AreEqual(0b11ul, hdr->SetMask.Word0);
            NAssert.AreEqual(0ul, hdr->SetMask.Word1);
            NAssert.AreEqual(0ul, hdr->SetMask.Word2);
            NAssert.AreEqual(0ul, hdr->SetMask.Word3);

            refs.Dispose();
        }

        [Test]
        public void AddEntity_AfterSubmit_EntityExistsWithSetComponent()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);

            var refs = a.ReserveEntityHandles(3, Allocator.Temp);
            var nativeEcs = a.ToNative();

            nativeEcs.AddEntity(TestTags.Beta, sortKey: 2, refs[0]).Set(new TestInt { Value = 20 });
            nativeEcs.AddEntity(TestTags.Beta, sortKey: 0, refs[1]).Set(new TestInt { Value = 0 });
            nativeEcs.AddEntity(TestTags.Beta, sortKey: 1, refs[2]).Set(new TestInt { Value = 10 });

            refs.Dispose();
            a.Submit();

            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Beta));

            // Sorted by sortKey, so layout is [0, 10, 20].
            for (int i = 0; i < 3; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(i * 10, comp.Read.Value);
            }
        }

        [Test]
        public void AddEntity_AfterSubmit_DefaultsAppliedForUnsetComponents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.TwoCompBeta);
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Beta);

            var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var nativeEcs = a.ToNative();

            // Only set TestFloat; TestInt should default to 0.
            nativeEcs
                .AddEntity(TestTags.Beta, sortKey: 0, refs[0])
                .Set(new TestFloat { Value = 7.5f });

            refs.Dispose();
            a.Submit();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Beta));
            NAssert.AreEqual(0, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
            NAssert.AreEqual(7.5f, a.Component<TestFloat>(new EntityIndex(0, group)).Read.Value);
        }

        [Test]
        public void AddEntity_FireAndForget_EntitiesExistAfterSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var nativeEcs = a.ToNative();

            // No ReserveEntityHandles — fire-and-forget claims post-sort.
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 0).Set(new TestInt { Value = 100 });
            nativeEcs.AddEntity(TestTags.Alpha, sortKey: 1).Set(new TestInt { Value = 200 });

            a.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(100, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
            NAssert.AreEqual(200, a.Component<TestInt>(new EntityIndex(1, group)).Read.Value);
        }

        [Test]
        public void AddEntity_Generic_FireAndForget_EntitiesExistAfterSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            var nativeEcs = a.ToNative();

            nativeEcs.AddEntity<TestAlpha>(sortKey: 5).Set(new TestInt { Value = 99 });

            a.Submit();
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(99, a.Component<TestInt>(new EntityIndex(0, group)).Read.Value);
        }

        [Test]
        public void AddEntity_UnregisteredTagSet_Throws()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var nativeEcs = a.ToNative();

            // Beta isn't a registered group when only SimpleAlpha is the template.
            NAssert.Catch(() => nativeEcs.AddEntity(TestTags.Beta, sortKey: 0, refs[0]));

            refs.Dispose();
        }

        // Exercises the Phase 7a permissive-subset path through the full AddEntity
        // pipeline: a partial subset of a group's exact tag set (one that uniquely
        // resolves to a single group via the managed resolver) should also be
        // accepted by the Burst-path AddEntity and land the entity in the right
        // group.
        [Test]
        public void AddEntity_PartialSubsetTagSet_LandsInResolvedGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            // WithPartitions has two groups: {Gamma + PartitionA} and {Gamma + PartitionB}.
            // Querying just {PartitionA} uniquely resolves to the PartitionA group
            // under the managed permissive resolver — verify the native path mirrors that.
            var partitionAOnly = TagSet.FromTags(TestTags.PartitionA);
            var expectedGroup = a.WorldInfo.GetSingleGroupWithTags(partitionAOnly);

            var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            nativeEcs
                .AddEntity(partitionAOnly, sortKey: 0, refs[0])
                .Set(new TestInt { Value = 77 });
            refs.Dispose();

            a.Submit();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(partitionAOnly));
            var comp = a.Component<TestInt>(new EntityIndex(0, expectedGroup));
            NAssert.AreEqual(77, comp.Read.Value);
        }

        // Determinism cross-validation: two worlds running identical scrambled
        // workloads must produce byte-identical resulting component data. Catches
        // any non-determinism the fast-path drain might accidentally introduce
        // (e.g. thread-arrival ordering leaking through, dest-write ordering
        // depending on cell layout).
        [Test]
        public void AddEntity_DeterminismCrossValidation_SameWorkloadProducesIdenticalState()
        {
            // Run the same scrambled add sequence in two independent worlds and
            // assert per-slot byte equality afterwards.
            int[] sortKeys = { 7, 2, 9, 0, 4, 5, 3, 1, 8, 6 };

            int[] RunOnce()
            {
                using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
                var a = env.Accessor;
                var nativeEcs = a.ToNative();
                var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

                var refs = a.ReserveEntityHandles(sortKeys.Length, Allocator.Temp);
                for (int i = 0; i < sortKeys.Length; i++)
                {
                    nativeEcs
                        .AddEntity(TestTags.Alpha, sortKey: (uint)sortKeys[i], refs[i])
                        .Set(new TestInt { Value = sortKeys[i] * 11 });
                }
                refs.Dispose();
                a.Submit();

                var snapshot = new int[sortKeys.Length];
                for (int i = 0; i < sortKeys.Length; i++)
                {
                    snapshot[i] = a.Component<TestInt>(new EntityIndex(i, group)).Read.Value;
                }
                return snapshot;
            }

            var first = RunOnce();
            var second = RunOnce();

            NAssert.AreEqual(first.Length, second.Length);
            for (int i = 0; i < first.Length; i++)
            {
                NAssert.AreEqual(
                    first[i],
                    second[i],
                    $"Determinism violated at index {i}: first={first[i]}, second={second[i]}"
                );
            }
        }

        // Fire-and-forget AddEntity exercised from a parallel job. Verifies the
        // post-sort handle-claim flow (ClaimDeferredHandlesForNativeAdds) yields
        // a deterministic ordering even when slots arrive in the bags from
        // arbitrary worker threads. Also exercises the generic AddEntity<T>
        // overload from inside Burst — the route through TagSet.BurstableFromTags
        // means this no longer trips Burst AOT-eval of TagSet<T>.cctor.
        [Test]
        public void AddEntity_FireAndForget_FromParallelJob_DeterministicOrder()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            const int Count = 200;

            new FireAndForgetAddJob { World = a.ToNative() }
                .ScheduleParallel(a, Count)
                .Complete();

            // Manually drain the Trecs job scheduler since we used the external
            // ScheduleParallel(WorldAccessor, count) form rather than running
            // inside an ISystem (which auto-drains at phase boundaries). Without
            // this, Submit() asserts on outstanding jobs.
            a.JobScheduler.CompleteAllOutstanding();

            a.Submit();

            NAssert.AreEqual(Count, a.CountEntitiesWithTags(TestTags.Alpha));

            // sortKey == i, so post-sort entity at slot i should hold value i * 13.
            for (int i = 0; i < Count; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(
                    i * 13,
                    comp.Read.Value,
                    $"Expected value {i * 13} at index {i}, got {comp.Read.Value}"
                );
            }
        }

        [BurstCompile]
        partial struct FireAndForgetAddJob : IJobFor
        {
            [FromWorld]
            public NativeWorldAccessor World;

            public void Execute(int i)
            {
                World.AddEntity<TestAlpha>(sortKey: (uint)i).Set(new TestInt { Value = i * 13 });
            }
        }

        // 2-tag generic AddEntity exercised from Burst. Catches any
        // arity-specific BC0101 / BC1040 regressions in the BurstableFromTags
        // wider-arity overloads.
        [Test]
        public void AddEntity_TwoTagGeneric_FromParallelJob_LandsInCorrectGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithPartitions);
            var a = env.Accessor;
            var partitionATags = TagSet.FromTags(TestTags.Gamma, TestTags.PartitionA);
            var group = a.WorldInfo.GetSingleGroupWithTags(partitionATags);
            const int Count = 100;

            new TwoTagAddJob { World = a.ToNative() }
                .ScheduleParallel(a, Count)
                .Complete();

            a.JobScheduler.CompleteAllOutstanding();
            a.Submit();

            NAssert.AreEqual(Count, a.CountEntitiesWithTags(partitionATags));

            // sortKey == i, so post-sort entity at slot i should hold value i * 7.
            for (int i = 0; i < Count; i++)
            {
                var comp = a.Component<TestInt>(new EntityIndex(i, group));
                NAssert.AreEqual(i * 7, comp.Read.Value);
            }
        }

        [BurstCompile]
        partial struct TwoTagAddJob : IJobFor
        {
            [FromWorld]
            public NativeWorldAccessor World;

            public void Execute(int i)
            {
                World
                    .AddEntity<TestGamma, TestPartitionA>(sortKey: (uint)i)
                    .Set(new TestInt { Value = i * 7 });
            }
        }

        // Generic AddEntity overloads must surface the "no matching group"
        // error the same way the non-generic TagSet overload does — i.e.
        // throw immediately rather than silently dropping the entity. Beta is
        // a registered tag type, but no Beta group exists in a SimpleAlpha-
        // only environment, so the BurstableFromTags<TestBeta>() id has no
        // entry in the world's TagSet→group map.

        [Test]
        public void AddEntity_UnregisteredGenericTag_Throws_PreReserved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var nativeEcs = a.ToNative();

            NAssert.Catch(() => nativeEcs.AddEntity<TestBeta>(sortKey: 0, refs[0]));

            refs.Dispose();
        }

        [Test]
        public void AddEntity_UnregisteredGenericTag_Throws_FireAndForget()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            NAssert.Catch(() => nativeEcs.AddEntity<TestBeta>(sortKey: 0));
        }
    }
}
