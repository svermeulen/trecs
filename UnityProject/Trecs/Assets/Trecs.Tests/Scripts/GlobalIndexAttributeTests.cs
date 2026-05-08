using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// [WrapAsJob] flavour of the GlobalIndex coverage — exercises the
    /// AutoJobGenerator's <c>[GlobalIndex]</c> wiring (the
    /// <c>AutoJobInfo.NeedsGlobalIndexOffset</c> branch: emitted
    /// <c>_trecs_GlobalIndexOffset</c> field; per-group
    /// <c>_trecs_queryIndexOffset</c> accumulation in the generated
    /// ScheduleParallel overload). Each entity stamps its received global index
    /// into its own <c>TestInt</c>; the test asserts the union of stamped values
    /// covers <c>[0, total − 1]</c> exactly once, which only holds if the offset
    /// advances correctly between groups. Writing into per-entity components
    /// (rather than a shared NativeArray) sidesteps the cross-group write/write
    /// hazard that the manual job struct works around with
    /// <c>[NativeDisableContainerSafetyRestriction]</c> — we can't add that to
    /// a [PassThroughArgument] field. Lives at namespace scope (not nested in
    /// the test fixture) because AutoSystemGenerator only emits the World
    /// property / ISystemInternal wiring on top-level partial classes.
    /// </summary>
    partial class GatherIndicesWrapAsJobSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(QCatA))]
        [WrapAsJob]
        static void Gather(ref TestInt value, [GlobalIndex] int globalIndex)
        {
            value.Value = globalIndex;
        }

        public void Execute()
        {
            Gather();
        }
    }

    /// <summary>
    /// Runtime coverage for the <c>[GlobalIndex]</c> source-gen attribute. The attribute
    /// is only meaningful when iteration spans more than one group: a single-group test
    /// would pass with a buggy offset implementation since the offset is always zero on
    /// the first (and only) group. The two templates here both implement
    /// <c>ITagged&lt;QCatA&gt;</c>, so a <c>[ForEachEntity(Tag = typeof(QCatA))]</c>
    /// query hits both groups in sequence — the second group's call must receive
    /// indices offset by the first group's entity count for the global-index packing
    /// to be correct.
    /// </summary>
    [TestFixture]
    public partial class GlobalIndexAttributeTests
    {
        // Manual job struct using [ForEachEntity] + [GlobalIndex]. Mirrors
        // RendererSystem.BuildInstanceData (the only real-world usage today): a
        // partial struct with a NativeArray field, an [ForEachEntity] Execute method
        // that writes into the array at the supplied global-index slot, and a
        // generated ScheduleParallel(WorldAccessor) overload.
        partial struct GatherIndicesJob
        {
            // Both safety relaxations mirror the RendererSystem.BuildInstanceData
            // pattern. [NativeDisableParallelForRestriction] permits multiple parallel
            // workers (within one ScheduleParallel call) to write to distinct slots in
            // the same array. [NativeDisableContainerSafetyRestriction] permits the
            // SAME backing NativeArray to be passed through multiple per-group jobs in
            // sequence — the JobGenerator emits one ScheduleParallel call per matching
            // group, and Unity's safety system would otherwise flag the cross-group
            // job-vs-job overlap.
            [NativeDisableParallelForRestriction]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<int> Slots;

            [ForEachEntity(Tag = typeof(QCatA))]
            public void Execute(in TestInt _, [GlobalIndex] int globalIndex)
            {
                // Mark every reached slot. We assert below that every slot ended up
                // with exactly one mark and the index range covers [0, total − 1].
                Slots[globalIndex] = Slots[globalIndex] + 1;
            }
        }

        const int CountA = 4;
        const int CountAB = 3;

        [Test]
        public void GlobalIndex_AcrossTwoGroups_PacksUniquelyOverFullRange()
        {
            // Both QTestEntityA and QTestEntityAB carry ITagged<QCatA>, so they
            // resolve to two distinct groups under a single QCatA query.
            using var env = EcsTestHelper.CreateEnvironment(
                QTestEntityA.Template,
                QTestEntityAB.Template
            );
            var a = env.Accessor;

            for (int i = 0; i < CountA; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            for (int i = 0; i < CountAB; i++)
                a.AddEntity(Tag<QId2>.Value)
                    .Set(new TestInt { Value = 100 + i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            // Sanity: the QCatA query must see both groups (otherwise the test isn't
            // exercising the offset-accumulation path it claims to).
            int total = a.CountEntitiesWithTags(Tag<QCatA>.Value);
            NAssert.AreEqual(CountA + CountAB, total);

            var slots = new NativeArray<int>(total, Allocator.TempJob);
            try
            {
                new GatherIndicesJob { Slots = slots }
                    .ScheduleParallel(a)
                    .Complete();

                // Every slot in [0, total) must have been written exactly once.
                for (int i = 0; i < total; i++)
                {
                    NAssert.AreEqual(
                        1,
                        slots[i],
                        $"Slot {i} written {slots[i]} time(s); expected exactly once. "
                            + "If a slot is 0, the GlobalIndexOffset failed to advance "
                            + "between groups (or two groups overlapped); if >1, two entities "
                            + "produced the same global index."
                    );
                }
            }
            finally
            {
                slots.Dispose();
            }
        }

        [Test]
        public void GlobalIndex_OnWrapAsJob_AcrossTwoGroups_PacksUniquelyOverFullRange()
        {
            // Parallel coverage to GlobalIndex_AcrossTwoGroups_PacksUniquelyOverFullRange,
            // but driving the [WrapAsJob] (AutoJobGenerator) path. Exercises the
            // _trecs_GlobalIndexOffset field + per-group _trecs_queryIndexOffset
            // accumulation in the generated ScheduleParallel overloads. Without that
            // wiring the attribute would silently fall over to "all zeros" or fail at
            // generation time.
            //
            // Verification model: each entity stamps its received global index into
            // its own TestInt. After the tick, collect every TestInt across both
            // QCatA groups; the union must be exactly the set [0, total − 1] —
            // which only holds if the second group's offset advanced by the first
            // group's count.
            var system = new GatherIndicesWrapAsJobSystem();
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(system),
                QTestEntityA.Template,
                QTestEntityAB.Template
            );
            var a = env.Accessor;

            for (int i = 0; i < CountA; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = -1 })
                    .Set(new TestFloat())
                    .AssertComplete();
            for (int i = 0; i < CountAB; i++)
                a.AddEntity(Tag<QId2>.Value)
                    .Set(new TestInt { Value = -1 })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            int total = CountA + CountAB;
            // Sanity: the QCatA query must see both groups.
            NAssert.AreEqual(total, a.CountEntitiesWithTags(Tag<QCatA>.Value));

            // One fixed tick → system.Execute() schedules the [WrapAsJob] across
            // both QCatA groups; StepFixedFrames awaits the resulting handles.
            env.StepFixedFrames(1);

            // Collect every stamped value across both groups under QCatA.
            var stamps = new int[total];
            int idx = 0;
            var groupA = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var bufA = a.ComponentBuffer<TestInt>(groupA).Read;
            for (int i = 0; i < CountA; i++)
                stamps[idx++] = bufA[i].Value;
            var groupAB = a.WorldInfo.GetSingleGroupWithTags(Tag<QId2>.Value);
            var bufAB = a.ComponentBuffer<TestInt>(groupAB).Read;
            for (int i = 0; i < CountAB; i++)
                stamps[idx++] = bufAB[i].Value;

            // Every slot in [0, total) must appear exactly once across the union of
            // stamps. If the second group's GlobalIndexOffset failed to advance,
            // two entities (one per group) would receive the same low index and
            // some high index would be missing.
            Array.Sort(stamps);
            for (int i = 0; i < total; i++)
            {
                NAssert.AreEqual(
                    i,
                    stamps[i],
                    $"Sorted stamp[{i}] = {stamps[i]}; expected {i}. "
                        + "If a value is missing or duplicated, the [WrapAsJob] "
                        + "GlobalIndexOffset failed to advance between groups."
                );
            }
        }
    }
}
