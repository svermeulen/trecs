using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs
{
    /// <summary>
    /// Burst-compiled job that drains per-thread <see cref="AtomicNativeBags"/> produced by
    /// <see cref="NativeSetCommandBuffer{TSet}"/> into the actual <see cref="SetGroupEntry"/>
    /// data, making the writes visible to subsequent reader jobs.
    ///
    /// Scheduled eagerly after every writer job by
    /// <see cref="JobGenSchedulingExtensions.TrackNativeSetCommandBufferDepsForJob{TSet}"/>,
    /// and tracked as the new writer so readers naturally depend on it.
    ///
    /// When <see cref="RequireDeterministic"/> is true, entries are collected, sorted by
    /// (GroupIndex, Index), then applied — ensuring deterministic iteration order regardless
    /// of thread scheduling. Removes are always processed before adds.
    /// </summary>
    [BurstCompile]
    struct SetFlushJob : IJob
    {
        public AtomicNativeBags AddQueue;
        public AtomicNativeBags RemoveQueue;

        [NativeDisableContainerSafetyRestriction]
        public NativeList<SetGroupEntry> EntriesPerGroup;

        public bool RequireDeterministic;

        public void Execute()
        {
            if (RequireDeterministic)
            {
                FlushDeterministic();
            }
            else
            {
                FlushNonDeterministic();
            }
        }

        void FlushDeterministic()
        {
            var allRemoves = new NativeList<EntityIndex>(64, Allocator.Temp);
            for (int i = 0; i < RemoveQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref RemoveQueue.GetBag(i);
                while (!bag.IsEmpty)
                    allRemoves.Add(bag.Dequeue<EntityIndex>());
            }
            allRemoves.Sort();
            for (int i = 0; i < allRemoves.Length; i++)
            {
                var entityIndex = allRemoves[i];
                EntriesPerGroup[entityIndex.GroupIndex.Index].Remove(entityIndex.Index);
            }
            allRemoves.Dispose();

            var allAdds = new NativeList<EntityIndex>(64, Allocator.Temp);
            for (int i = 0; i < AddQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref AddQueue.GetBag(i);
                while (!bag.IsEmpty)
                    allAdds.Add(bag.Dequeue<EntityIndex>());
            }
            allAdds.Sort();
            for (int i = 0; i < allAdds.Length; i++)
            {
                var entityIndex = allAdds[i];
                EntriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
            }
            allAdds.Dispose();
        }

        void FlushNonDeterministic()
        {
            for (int i = 0; i < RemoveQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref RemoveQueue.GetBag(i);
                while (!bag.IsEmpty)
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    EntriesPerGroup[entityIndex.GroupIndex.Index].Remove(entityIndex.Index);
                }
            }

            for (int i = 0; i < AddQueue.ThreadSlotCount; i++)
            {
                ref var bag = ref AddQueue.GetBag(i);
                while (!bag.IsEmpty)
                {
                    var entityIndex = bag.Dequeue<EntityIndex>();
                    EntriesPerGroup[entityIndex.GroupIndex.Index].Add(entityIndex.Index);
                }
            }
        }
    }
}
