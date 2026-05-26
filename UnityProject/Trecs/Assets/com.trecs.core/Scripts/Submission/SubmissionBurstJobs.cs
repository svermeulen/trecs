using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs.Internal
{
    // One entry per fast-path slot to be drained. Built sequentially on the main
    // thread in DrainFastAddBags before scheduling FastAddFillJob. Pointer fields
    // stored as long so the struct is blittable for Burst.
    internal struct FastAddFillSlotWork
    {
        public long SlotPtr;
        public int DestIdx;
        public int GroupIdx;
    }

    // Per-component destination info for one group. Flattened across groups in a
    // single NativeArray; GroupComponentDestStartIdx[groupIdx] gives the starting
    // index for group's components.
    internal struct FastAddComponentDest
    {
        public long ArrayPtr;
        public int ElementSize;
    }

    // Burst-compiled parallel fill for the fast-path add drain. One iteration per
    // pending entity. Reads the slot header to recover SetMask, then for each
    // component on the entity's template writes either: the user-set bytes from
    // the slot, MemClear for zero-default components, or the prototype default
    // bytes for non-zero-default unset components.
    [BurstCompile]
    internal struct FastAddFillJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<FastAddFillSlotWork> Slots;

        [ReadOnly]
        public NativeArray<NativeTemplateLayoutHeader> LayoutHeaders;

        [ReadOnly]
        public NativeArray<NativeComponentLayoutEntry> LayoutEntries;

        [ReadOnly]
        public NativeArray<FastAddComponentDest> ComponentDests;

        [ReadOnly]
        public NativeArray<int> GroupComponentDestStartIdx;

        [NativeDisableUnsafePtrRestriction]
        public long DefaultBytesBase;

        public int HeaderSize;

        public void Execute(int idx)
        {
            unsafe
            {
                var work = Slots[idx];
                var slotPtr = (byte*)work.SlotPtr;
                var hdr = (FastAddSlotHeader*)slotPtr;
                var setMask = hdr->SetMask;
                int gIdx = work.GroupIdx;
                var layoutHeader = LayoutHeaders[gIdx];
                var zeroDefaultMask = layoutHeader.ZeroDefaultMask;
                byte* slotComponentBytes = slotPtr + HeaderSize;
                byte* groupDefaultBytes = (byte*)DefaultBytesBase + layoutHeader.DefaultBytesOffset;
                int compDestBase = GroupComponentDestStartIdx[gIdx];

                for (int ci = 0; ci < layoutHeader.ComponentCount; ci++)
                {
                    var entry = LayoutEntries[layoutHeader.FirstComponentIndex + ci];
                    var dest = ComponentDests[compDestBase + ci];
                    byte* destPtr = (byte*)dest.ArrayPtr + (long)work.DestIdx * dest.ElementSize;

                    if (setMask.IsSet(ci))
                    {
                        UnsafeUtility.MemCpy(
                            destPtr,
                            slotComponentBytes + entry.ByteOffset,
                            entry.ByteSize
                        );
                    }
                    else if (zeroDefaultMask.IsSet(ci))
                    {
                        UnsafeUtility.MemClear(destPtr, entry.ByteSize);
                    }
                    else
                    {
                        UnsafeUtility.MemCpy(
                            destPtr,
                            groupDefaultBytes + entry.ByteOffset,
                            entry.ByteSize
                        );
                    }
                }
            }
        }
    }

    readonly struct SwapEntryComparer : IComparer<SwapEntry>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(SwapEntry x, SwapEntry y)
        {
            int c = x.EntityIndex.CompareTo(y.EntityIndex);
            if (c != 0)
                return c;
            c = x.ToGroup.CompareTo(y.ToGroup);
            if (c != 0)
                return c;
            return x.AccessorId.CompareTo(y.AccessorId);
        }
    }

    [BurstCompile]
    struct SortSwapsJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public long Ptr;
        public int Count;

        public void Execute()
        {
            unsafe
            {
                NativeSortExtension.Sort((SwapEntry*)Ptr, Count, new SwapEntryComparer());
            }
        }
    }

    readonly struct NativeTagOpComparer : IComparer<NativeTagOp>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(NativeTagOp x, NativeTagOp y)
        {
            return x.EntityIndex.CompareTo(y.EntityIndex);
        }
    }

    [BurstCompile]
    struct SortTagOpsJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public long Ptr;
        public int Count;

        public void Execute()
        {
            unsafe
            {
                NativeSortExtension.Sort((NativeTagOp*)Ptr, Count, new NativeTagOpComparer());
            }
        }
    }

    readonly struct RemovalEntryComparer : IComparer<RemovalEntry>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(RemovalEntry x, RemovalEntry y)
        {
            int c = x.EntityIndex.CompareTo(y.EntityIndex);
            if (c != 0)
                return c;
            return x.AccessorId.CompareTo(y.AccessorId);
        }
    }

    /// <summary>
    /// Burst-compiled in-place reorder of a contiguous range of a type-erased
    /// buffer according to a permutation. <c>permutation[i]</c> gives the source
    /// index (relative to <c>StartIndex</c>) for destination position
    /// <c>StartIndex + i</c>. The scratch buffer must hold at least
    /// <c>Count * ElementSize</c> bytes; it is overwritten with the range's
    /// pre-reorder contents and read scatter-wise to perform the permutation.
    /// </summary>
    [BurstCompile]
    unsafe struct ReorderRangeJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public long BufferPtr;

        [NativeDisableUnsafePtrRestriction]
        public long ScratchPtr;

        [NativeDisableUnsafePtrRestriction]
        public long PermutationPtr;
        public int ElementSize;
        public int StartIndex;
        public int Count;

        public void Execute()
        {
            var range = (byte*)BufferPtr + (long)StartIndex * ElementSize;
            var scratch = (byte*)ScratchPtr;
            var perm = (int*)PermutationPtr;

            UnsafeUtility.MemCpy(scratch, range, (long)Count * ElementSize);

            for (int i = 0; i < Count; i++)
            {
                UnsafeUtility.MemCpy(
                    range + (long)i * ElementSize,
                    scratch + (long)perm[i] * ElementSize,
                    ElementSize
                );
            }
        }
    }

    [BurstCompile]
    struct SortRemovalsJob : IJob
    {
        [NativeDisableUnsafePtrRestriction]
        public long Ptr;
        public int Count;

        public void Execute()
        {
            unsafe
            {
                NativeSortExtension.Sort((RemovalEntry*)Ptr, Count, new RemovalEntryComparer());
            }
        }
    }

    readonly struct DescendingIntComparer : IComparer<int>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(int a, int b) => b.CompareTo(a);
    }

    /// <summary>
    /// Burst-compiled descending sort of a <see cref="NativeList{Int32}"/>.
    /// Used by Remove Sort+Plan to drop the managed comparer-based sort cost
    /// on bulk-spike workloads. The accompanying swap-back plan build
    /// (ComputeSwapBackPlanDescending) intentionally stays managed because
    /// its output is a <c>IterableDictionary&lt;int,int&gt;</c> — insertion
    /// order matters for the consumer
    /// <see cref="Trecs.Internal.EntityHandleMap.BatchUpdateIndexAfterSwapBack"/>,
    /// and NativeHashMap iterates in bucket order rather than insertion order.
    /// </summary>
    /// <summary>
    /// Burst-compiled step (a) of Remove Refs:
    ///   1. Build tailMap (originalSlot → tailSlot) from the sorted-descending
    ///      remove indices.
    ///   2. Walk entityHandlesToRemove in submission order, clear each entity's
    ///      slot in <c>GroupList</c> (the per-group reverse-map), and append
    ///      a <see cref="Trecs.DeferredHandleFreeEntry"/> capture to
    ///      <c>DeferredFreesOut</c> for the post-callback finalize.
    /// <para>
    /// <c>GroupList</c> is the relevant per-group <see cref="UnsafeList{Int32}"/>
    /// passed by value — the underlying buffer pointer is shared with the
    /// caller's <c>NativeList&lt;UnsafeList&lt;int&gt;&gt;._entityIndexToReferenceMap</c>,
    /// so element writes propagate. Capacity/length on the local copy is
    /// stale after writes but unused.
    /// </para>
    /// </summary>
    [BurstCompile]
    unsafe struct RemoveRefsCaptureJob : IJob
    {
        public UnsafeList<int> GroupList;

        [ReadOnly]
        public NativeList<int> RemoveIndicesSubmissionOrder;

        [ReadOnly]
        public NativeList<int> SortedDescendingIndices;
        public NativeHashMap<int, int> TailMap;
        public NativeList<DeferredHandleFreeEntry> DeferredFreesOut;
        public int OriginalCount;
        public GroupIndex FromGroup;

        public void Execute()
        {
            TailMap.Clear();
            for (int i = 0; i < SortedDescendingIndices.Length; i++)
            {
                TailMap.Add(SortedDescendingIndices[i], OriginalCount - 1 - i);
            }

            for (int i = 0; i < RemoveIndicesSubmissionOrder.Length; i++)
            {
                var entityArrayIndex = RemoveIndicesSubmissionOrder[i];
                var id = GroupList[entityArrayIndex];
                // Format args from the pre-Burst variant of this check were
                // dropped because TrecsDebugAssert's ThrowManagedFormatted is
                // [BurstDiscard] — from inside a Burst job, only the static
                // "Assert hit!" InvalidOperationException is observable. To
                // recover diagnostic context, run the failing test under a
                // managed (editor playmode) bench or temporarily move this
                // check out of the job.
                TrecsDebugAssert.That(
                    id != 0,
                    "RemoveRefsCaptureJob: null EntityHandle in groupList (entity already removed or never had a handle)"
                );
                GroupList[entityArrayIndex] = 0;
                var tailSlot = TailMap[entityArrayIndex];
                DeferredFreesOut.Add(
                    new DeferredHandleFreeEntry
                    {
                        Group = FromGroup,
                        Id = id,
                        TailSlot = tailSlot,
                    }
                );
            }
        }
    }

    /// <summary>
    /// Burst-compiled step (c) of Remove Refs: relocate the (id, tailSlot)
    /// captures (added by <see cref="RemoveRefsCaptureJob"/>) into their tail
    /// positions so <c>EntityIndex.ToHandle</c> resolves the removed entities
    /// during OnRemoved fan-out. Writes both the per-group reverse-map slot
    /// and the forward <see cref="EntityHandleMap"/> entry. Walks only the
    /// per-group slice <c>[SliceStart, DeferredFrees.Length)</c>.
    /// </summary>
    [BurstCompile]
    unsafe struct RemoveRefsRelocateJob : IJob
    {
        public UnsafeList<int> GroupList;
        public NativeList<EntityHandleMapElement> EntityHandleMap;

        [ReadOnly]
        public NativeList<DeferredHandleFreeEntry> DeferredFrees;
        public int SliceStart;
        public GroupIndex FromGroup;

        public void Execute()
        {
            for (int i = SliceStart; i < DeferredFrees.Length; i++)
            {
                var entry = DeferredFrees[i];
                GroupList[entry.TailSlot] = entry.Id;

                ref var element = ref EntityHandleMap.ElementAt(entry.Id - 1);
                element.Index = entry.TailSlot;
                element.GroupIndex = FromGroup;
            }
        }
    }

    [BurstCompile]
    struct SortIntsDescendingJob : IJob
    {
        public NativeList<int> Indices;

        public void Execute()
        {
            Indices.Sort(new DescendingIntComparer());
        }
    }

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
                            dst + (long)dstIdx * elemSize,
                            src + (long)fromIdx * elemSize,
                            elemSize
                        );

                        if (fromIdx != currentSourceCount)
                        {
                            UnsafeUtility.MemCpy(
                                src + (long)fromIdx * elemSize,
                                src + (long)currentSourceCount * elemSize,
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
                        dst + (long)(DstBaseCount + entityIdx) * ElementSize,
                        src + (long)fromIdx * ElementSize,
                        ElementSize
                    );

                    if (fromIdx != currentSourceCount)
                    {
                        UnsafeUtility.MemCpy(
                            src + (long)fromIdx * ElementSize,
                            src + (long)currentSourceCount * ElementSize,
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

                        var removedSlot = ptr + (long)indexToRemove * elemSize;
                        var lastSlot = ptr + (long)currentCount * elemSize;

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

                    var removedSlot = ptr + (long)indexToRemove * ElementSize;
                    var lastSlot = ptr + (long)currentCount * ElementSize;

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

    [BurstCompile]
    struct BatchUpdateEntityHandlesJob : IJob
    {
        public UnsafeList<int> FromGroupList;
        public UnsafeList<int> ToGroupList;

        [NativeDisableContainerSafetyRestriction]
        public NativeList<EntityHandleMapElement> EntityHandleMap;

        [ReadOnly]
        public NativeList<MoveInfoEntry> EntriesToMove;

        public GroupIndex ToGroup;

        public void Execute()
        {
            for (int i = 0; i < EntriesToMove.Length; i++)
            {
                var entry = EntriesToMove[i];
                var fromIndex = entry.EntityIndex;
                var toIndex = entry.Info.ToIndex;

                var id = FromGroupList[fromIndex];
                FromGroupList[fromIndex] = 0;

                ref var element = ref EntityHandleMap.ElementAt(id - 1);
                element.Index = toIndex;
                element.GroupIndex = ToGroup;

                ToGroupList[toIndex] = id;
            }
        }
    }
}
