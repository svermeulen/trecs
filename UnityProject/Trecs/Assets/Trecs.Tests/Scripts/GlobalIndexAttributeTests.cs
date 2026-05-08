using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Runtime coverage for the <c>[GlobalIndex]</c> source-gen attribute. The attribute
    /// is only meaningful when iteration spans more than one group: a single-group test
    /// would pass with a buggy offset implementation since the offset is always zero on
    /// the first (and only) group. The two templates here both implement
    /// <c>IHasTags&lt;QCatA&gt;</c>, so a <c>[ForEachEntity(Tag = typeof(QCatA))]</c>
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
            // Both QTestEntityA and QTestEntityAB carry IHasTags<QCatA>, so they
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
    }
}
