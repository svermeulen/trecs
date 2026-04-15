using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs.Internal
{
    /// <summary>
    /// Burst-compiled job for performing swap-back data movement during entity moves.
    /// Processes all component types in a single job (used when parallelizing across
    /// component types is not beneficial due to small batch sizes).
    /// </summary>
    [BurstCompile]
    struct MoveDataJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<long> srcPtrs;

        [NativeDisableUnsafePtrRestriction]
        public NativeArray<long> dstPtrs;
        public NativeArray<int> elementSizes;
        public NativeArray<int> dstBaseCounts;

        [ReadOnly]
        public NativeArray<int> resolvedFromIndices;

        public int numComponents;
        public int numEntities;
        public int sourceCount;

        public void Execute()
        {
            var currentSourceCount = sourceCount;

            for (int entityIdx = 0; entityIdx < numEntities; entityIdx++)
            {
                var fromIdx = resolvedFromIndices[entityIdx];
                currentSourceCount--;

                for (int compIdx = 0; compIdx < numComponents; compIdx++)
                {
                    unsafe
                    {
                        var src = (byte*)srcPtrs[compIdx];
                        var dst = (byte*)dstPtrs[compIdx];
                        var elemSize = elementSizes[compIdx];
                        var dstIdx = dstBaseCounts[compIdx] + entityIdx;

                        UnsafeUtility.MemCpy(
                            dst + dstIdx * elemSize,
                            src + fromIdx * elemSize,
                            elemSize
                        );

                        if (fromIdx != currentSourceCount)
                        {
                            UnsafeUtility.MemCpy(
                                src + fromIdx * elemSize,
                                src + currentSourceCount * elemSize,
                                elemSize
                            );
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Burst-compiled job for a single component type's move data movement.
    /// Multiple instances are scheduled in parallel (one per component type).
    /// </summary>
    [BurstCompile]
    struct MoveDataPerComponentJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public long srcPtr;

        [NativeDisableUnsafePtrRestriction]
        public long dstPtr;
        public int elementSize;
        public int dstBaseCount;
        public int sourceCount;
        public int numEntities;

        [ReadOnly]
        public NativeArray<int> resolvedFromIndices;

        public void Execute()
        {
            var currentSourceCount = sourceCount;

            unsafe
            {
                var src = (byte*)srcPtr;
                var dst = (byte*)dstPtr;

                for (int entityIdx = 0; entityIdx < numEntities; entityIdx++)
                {
                    var fromIdx = resolvedFromIndices[entityIdx];
                    currentSourceCount--;

                    UnsafeUtility.MemCpy(
                        dst + (dstBaseCount + entityIdx) * elementSize,
                        src + fromIdx * elementSize,
                        elementSize
                    );

                    if (fromIdx != currentSourceCount)
                    {
                        UnsafeUtility.MemCpy(
                            src + fromIdx * elementSize,
                            src + currentSourceCount * elementSize,
                            elementSize
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Burst-compiled job for performing swap-back data movement during entity removals.
    /// Processes all component types in a single job.
    /// </summary>
    [BurstCompile]
    struct RemoveDataJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public NativeArray<long> arrayPtrs;
        public NativeArray<int> elementSizes;

        [ReadOnly]
        public NativeArray<int> sortedDescendingIndices;

        public int numComponents;
        public int numRemovals;
        public int sourceCount;
        public int maxElementSize;

        public void Execute()
        {
            var currentCount = sourceCount;

            unsafe
            {
                var temp = (byte*)UnsafeUtility.Malloc(maxElementSize, 16, Allocator.Temp);

                for (int removalIdx = 0; removalIdx < numRemovals; removalIdx++)
                {
                    var indexToRemove = sortedDescendingIndices[removalIdx];
                    currentCount--;

                    for (int compIdx = 0; compIdx < numComponents; compIdx++)
                    {
                        var ptr = (byte*)arrayPtrs[compIdx];
                        var elemSize = elementSizes[compIdx];

                        var removedSlot = ptr + indexToRemove * elemSize;
                        var lastSlot = ptr + currentCount * elemSize;

                        if (indexToRemove != currentCount)
                        {
                            UnsafeUtility.MemCpy(temp, removedSlot, elemSize);
                            UnsafeUtility.MemCpy(removedSlot, lastSlot, elemSize);
                            UnsafeUtility.MemCpy(lastSlot, temp, elemSize);
                        }
                    }
                }

                UnsafeUtility.Free(temp, Allocator.Temp);
            }
        }
    }

    /// <summary>
    /// Burst-compiled job for a single component type's remove data movement.
    /// Multiple instances are scheduled in parallel (one per component type).
    /// </summary>
    [BurstCompile]
    struct RemoveDataPerComponentJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public long arrayPtr;
        public int elementSize;
        public int sourceCount;
        public int numRemovals;

        [ReadOnly]
        public NativeArray<int> sortedDescendingIndices;

        public void Execute()
        {
            var currentCount = sourceCount;

            unsafe
            {
                var ptr = (byte*)arrayPtr;

                // Allocate temp buffer for swap (element-sized)
                var temp = (byte*)UnsafeUtility.Malloc(elementSize, 16, Allocator.Temp);

                for (int removalIdx = 0; removalIdx < numRemovals; removalIdx++)
                {
                    var indexToRemove = sortedDescendingIndices[removalIdx];
                    currentCount--;

                    var removedSlot = ptr + indexToRemove * elementSize;
                    var lastSlot = ptr + currentCount * elementSize;

                    if (indexToRemove != currentCount)
                    {
                        UnsafeUtility.MemCpy(temp, removedSlot, elementSize);
                        UnsafeUtility.MemCpy(removedSlot, lastSlot, elementSize);
                        UnsafeUtility.MemCpy(lastSlot, temp, elementSize);
                    }
                }

                UnsafeUtility.Free(temp, Allocator.Temp);
            }
        }
    }
}
