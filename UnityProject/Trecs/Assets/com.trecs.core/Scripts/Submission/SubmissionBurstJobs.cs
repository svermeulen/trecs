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
        public NativeArray<long> SrcPtrs;

        [NativeDisableUnsafePtrRestriction]
        public NativeArray<long> DstPtrs;
        public NativeArray<int> ElementSizes;
        public NativeArray<int> DstBaseCounts;

        [ReadOnly]
        public NativeArray<int> ResolvedFromIndices;

        public int NumComponents;
        public int NumEntities;
        public int SourceCount;

        public void Execute()
        {
            var currentSourceCount = SourceCount;

            for (int entityIdx = 0; entityIdx < NumEntities; entityIdx++)
            {
                var fromIdx = ResolvedFromIndices[entityIdx];
                currentSourceCount--;

                for (int compIdx = 0; compIdx < NumComponents; compIdx++)
                {
                    unsafe
                    {
                        var src = (byte*)SrcPtrs[compIdx];
                        var dst = (byte*)DstPtrs[compIdx];
                        var elemSize = ElementSizes[compIdx];
                        var dstIdx = DstBaseCounts[compIdx] + entityIdx;

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
        public long SrcPtr;

        [NativeDisableUnsafePtrRestriction]
        public long DstPtr;
        public int ElementSize;
        public int DstBaseCount;
        public int SourceCount;
        public int NumEntities;

        [ReadOnly]
        public NativeArray<int> ResolvedFromIndices;

        public void Execute()
        {
            var currentSourceCount = SourceCount;

            unsafe
            {
                var src = (byte*)SrcPtr;
                var dst = (byte*)DstPtr;

                for (int entityIdx = 0; entityIdx < NumEntities; entityIdx++)
                {
                    var fromIdx = ResolvedFromIndices[entityIdx];
                    currentSourceCount--;

                    UnsafeUtility.MemCpy(
                        dst + (DstBaseCount + entityIdx) * ElementSize,
                        src + fromIdx * ElementSize,
                        ElementSize
                    );

                    if (fromIdx != currentSourceCount)
                    {
                        UnsafeUtility.MemCpy(
                            src + fromIdx * ElementSize,
                            src + currentSourceCount * ElementSize,
                            ElementSize
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
        public NativeArray<long> ArrayPtrs;
        public NativeArray<int> ElementSizes;

        [ReadOnly]
        public NativeArray<int> SortedDescendingIndices;

        public int NumComponents;
        public int NumRemovals;
        public int SourceCount;
        public int MaxElementSize;

        public void Execute()
        {
            var currentCount = SourceCount;

            unsafe
            {
                var temp = (byte*)UnsafeUtility.Malloc(MaxElementSize, 16, Allocator.Temp);

                for (int removalIdx = 0; removalIdx < NumRemovals; removalIdx++)
                {
                    var indexToRemove = SortedDescendingIndices[removalIdx];
                    currentCount--;

                    for (int compIdx = 0; compIdx < NumComponents; compIdx++)
                    {
                        var ptr = (byte*)ArrayPtrs[compIdx];
                        var elemSize = ElementSizes[compIdx];

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
        public long ArrayPtr;
        public int ElementSize;
        public int SourceCount;
        public int NumRemovals;

        [ReadOnly]
        public NativeArray<int> SortedDescendingIndices;

        public void Execute()
        {
            var currentCount = SourceCount;

            unsafe
            {
                var ptr = (byte*)ArrayPtr;

                // Allocate temp buffer for swap (element-sized)
                var temp = (byte*)UnsafeUtility.Malloc(ElementSize, 16, Allocator.Temp);

                for (int removalIdx = 0; removalIdx < NumRemovals; removalIdx++)
                {
                    var indexToRemove = SortedDescendingIndices[removalIdx];
                    currentCount--;

                    var removedSlot = ptr + indexToRemove * ElementSize;
                    var lastSlot = ptr + currentCount * ElementSize;

                    if (indexToRemove != currentCount)
                    {
                        UnsafeUtility.MemCpy(temp, removedSlot, ElementSize);
                        UnsafeUtility.MemCpy(removedSlot, lastSlot, ElementSize);
                        UnsafeUtility.MemCpy(lastSlot, temp, ElementSize);
                    }
                }

                UnsafeUtility.Free(temp, Allocator.Temp);
            }
        }
    }
}
