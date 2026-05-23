using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;

namespace Trecs.Internal
{
    // Per-thread × per-group raw-byte staging buffer for the Burst-friendly AddEntity
    // fast path. Each (thread, group) cell is an UnsafeList<byte> that the writer
    // appends fixed-size slots to — slot size is determined by the group's template
    // at world-init time and stored in _slotSizes[group]. Each cell is owned by
    // exactly one writer thread so the append is contention-free.
    //
    // Memory layout: one big contiguous block holds (threadsCount × groupsCount)
    // UnsafeList<byte> structs. Each UnsafeList owns its own grow-on-demand buffer.
    //
    // Dequeue/iteration during the drain pipeline is single-threaded by convention.
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal unsafe struct PerGroupAddBags : IDisposable
    {
        int _threadsCount;
        int _groupsCount;

        [NoAlias]
        [NativeDisableUnsafePtrRestriction]
        UnsafeList<byte>* _cells;

        // Slot size per group (indexed by groupIdx). Stored natively so the
        // append path is reachable from Burst jobs without any managed lookups.
        [NoAlias]
        [NativeDisableUnsafePtrRestriction]
        int* _slotSizes;

        AllocatorManager.AllocatorHandle _allocator;

        public int ThreadSlotCount => _threadsCount;
        public int GroupCount => _groupsCount;

        // slotSizes[i] is the per-entity byte count for groupIdx i. Must be > 0 for
        // every group that will ever receive a Burst-path AddEntity call. Copied into
        // native memory; the caller's array is not retained.
        public static PerGroupAddBags Create(
            ReadOnlySpan<int> slotSizes,
            AllocatorManager.AllocatorHandle allocator
        )
        {
            var result = new PerGroupAddBags();
            result._allocator = allocator;
            result._threadsCount = JobsUtility.MaxJobThreadCount + 1;
            result._groupsCount = slotSizes.Length;

            var cellSize = Unsafe.SizeOf<UnsafeList<byte>>();
            var cellCount = result._threadsCount * result._groupsCount;
            var allocationSize = cellSize * cellCount;

            var ptr = (byte*)UnsafeUtility.Malloc(allocationSize, 16, allocator.ToAllocator);
            UnsafeUtility.MemClear(ptr, allocationSize);

            for (int i = 0; i < cellCount; i++)
            {
                var cellPtr = (UnsafeList<byte>*)(ptr + cellSize * i);
                *cellPtr = new UnsafeList<byte>(0, allocator);
            }

            result._cells = (UnsafeList<byte>*)ptr;

            int sizeArrayBytes = sizeof(int) * result._groupsCount;
            result._slotSizes = (int*)
                UnsafeUtility.Malloc(sizeArrayBytes, 16, allocator.ToAllocator);
            for (int g = 0; g < result._groupsCount; g++)
            {
                result._slotSizes[g] = slotSizes[g];
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref UnsafeList<byte> GetCell(int threadIdx, int groupIdx)
        {
#if DEBUG
            if (_cells == null)
                throw new TrecsException("using invalid PerGroupAddBags");
            if ((uint)threadIdx >= (uint)_threadsCount)
                throw new TrecsException("threadIdx out of range");
            if ((uint)groupIdx >= (uint)_groupsCount)
                throw new TrecsException("groupIdx out of range");
#endif

            int linearIndex = threadIdx * _groupsCount + groupIdx;
            return ref Unsafe.AsRef<UnsafeList<byte>>(
                Unsafe.Add<UnsafeList<byte>>(_cells, linearIndex)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int SlotSize(int groupIdx)
        {
#if DEBUG
            if ((uint)groupIdx >= (uint)_groupsCount)
                throw new TrecsException("groupIdx out of range");
#endif
            return _slotSizes[groupIdx];
        }

        // Appends one slot's worth of uninitialised bytes to the (thread, group) cell
        // and returns a raw pointer to the new bytes. The slot's exact size is the
        // per-group value passed in at Create time.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte* AppendSlot(int threadIdx, int groupIdx)
        {
#if DEBUG
            if ((uint)groupIdx >= (uint)_groupsCount)
                throw new TrecsException("groupIdx out of range");
#endif
            int slotSize = _slotSizes[groupIdx];
            ref var cell = ref GetCell(threadIdx, groupIdx);
            int prevLength = cell.Length;
            cell.Resize(prevLength + slotSize, NativeArrayOptions.UninitializedMemory);
            return cell.Ptr + prevLength;
        }

        public void Clear()
        {
#if DEBUG
            if (_cells == null)
                throw new TrecsException("using invalid PerGroupAddBags");
#endif
            int cellCount = _threadsCount * _groupsCount;
            for (int i = 0; i < cellCount; i++)
            {
                ref var cell = ref Unsafe.AsRef<UnsafeList<byte>>(
                    Unsafe.Add<UnsafeList<byte>>(_cells, i)
                );
                cell.Clear();
            }
        }

        public void Dispose()
        {
#if DEBUG
            if (_cells == null)
                throw new TrecsException("using invalid PerGroupAddBags");
#endif
            int cellCount = _threadsCount * _groupsCount;
            for (int i = 0; i < cellCount; i++)
            {
                ref var cell = ref Unsafe.AsRef<UnsafeList<byte>>(
                    Unsafe.Add<UnsafeList<byte>>(_cells, i)
                );
                cell.Dispose();
            }
            UnsafeUtility.Free(_cells, _allocator.ToAllocator);
            UnsafeUtility.Free(_slotSizes, _allocator.ToAllocator);
            _cells = null;
            _slotSizes = null;
        }
    }
}
