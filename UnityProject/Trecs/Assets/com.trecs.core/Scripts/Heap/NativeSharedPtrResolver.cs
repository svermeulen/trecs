using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [StructLayout(LayoutKind.Sequential)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NativeSharedHeapSideTableEntry
    {
        public IntPtr Address;
        public int TypeHash;
        public byte Generation;
        public byte InUse;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public AtomicSafetyHandle Safety;
#endif
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSharedHeapPayload
    {
        public BlobId BlobId;
        public int TypeHash;
        public byte Generation;
        public byte InUse;
        public int RefCount;
    }

    /// <summary>
    /// Burst-compatible resolver for <see cref="NativeSharedPtr{T}"/> lookups inside jobs.
    /// Uses a chunked side-table directory identical in structure to <see cref="NativeHeapResolver"/>,
    /// enabling main-thread allocations concurrent with job reads.
    /// </summary>
    public readonly unsafe struct NativeSharedPtrResolver
    {
        [NativeDisableContainerSafetyRestriction]
        readonly NativeArray<IntPtr> _chunkDirectory;

        internal NativeSharedPtrResolver(NativeArray<IntPtr> chunkDirectory)
        {
            _chunkDirectory = chunkDirectory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeSharedHeapSideTableEntry ResolveEntry<T>(uint handle)
            where T : unmanaged
        {
            TrecsAssert.That(
                TryResolveEntry(handle, out var entry),
                "NativeSharedPtrResolver could not resolve handle {0} "
                    + "(null, out of range, freed, or stale generation)",
                handle
            );

            TrecsAssert.That(
                entry.TypeHash == TypeId<T>.Value.Value,
                "Type hash mismatch for handle {0}: stored {1} != requested {2}",
                handle,
                entry.TypeHash,
                TypeId<T>.Value.Value
            );

            return entry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryResolveEntry(uint handle, out NativeSharedHeapSideTableEntry entry)
        {
            if (handle == 0)
            {
                entry = default;
                return false;
            }

            DecodeHandle(handle, out var index, out var generation);

            var chunkIdx = (int)index >> NativeHeapResolver.ChunkSizeBits;
            if (chunkIdx >= _chunkDirectory.Length)
            {
                entry = default;
                return false;
            }

            var chunkPtr = (NativeSharedHeapSideTableEntry*)_chunkDirectory[chunkIdx].ToPointer();
            if (chunkPtr == null)
            {
                entry = default;
                return false;
            }

            entry = chunkPtr[(int)index & NativeHeapResolver.ChunkIndexMask];
            return entry.InUse == 1 && entry.Generation == generation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal NativeSharedHeapSideTableEntry ResolveEntryWithSlotPtr<T>(
            uint handle,
            out NativeSharedHeapSideTableEntry* slotPtr
        )
            where T : unmanaged
        {
            TrecsAssert.That(
                TryResolveEntryWithSlotPtr(handle, out var entry, out slotPtr),
                "NativeSharedPtrResolver could not resolve handle {0} "
                    + "(null, out of range, freed, or stale generation)",
                handle
            );

            TrecsAssert.That(
                entry.TypeHash == TypeId<T>.Value.Value,
                "Type hash mismatch for handle {0}: stored {1} != requested {2}",
                handle,
                entry.TypeHash,
                TypeId<T>.Value.Value
            );

            return entry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryResolveEntryWithSlotPtr(
            uint handle,
            out NativeSharedHeapSideTableEntry entry,
            out NativeSharedHeapSideTableEntry* slotPtr
        )
        {
            if (handle == 0)
            {
                entry = default;
                slotPtr = null;
                return false;
            }

            DecodeHandle(handle, out var index, out var generation);

            var chunkIdx = (int)index >> NativeHeapResolver.ChunkSizeBits;
            if (chunkIdx >= _chunkDirectory.Length)
            {
                entry = default;
                slotPtr = null;
                return false;
            }

            var chunkPtr = (NativeSharedHeapSideTableEntry*)_chunkDirectory[chunkIdx].ToPointer();
            if (chunkPtr == null)
            {
                entry = default;
                slotPtr = null;
                return false;
            }

            slotPtr = chunkPtr + ((int)index & NativeHeapResolver.ChunkIndexMask);
            entry = *slotPtr;
            return entry.InUse == 1 && entry.Generation == generation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void DecodeHandle(uint handle, out uint index, out byte generation)
        {
            index = handle & NativeHeapResolver.IndexMask;
            generation = (byte)(handle >> NativeHeapResolver.GenerationShift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint EncodeHandle(uint generation, uint index)
        {
            TrecsDebugAssert.That(
                index >= 1 && index <= NativeHeapResolver.MaxIndex,
                "Side-table index {0} out of range",
                index
            );
            TrecsDebugAssert.That(
                generation >= 1 && generation <= 0xFF,
                "Generation {0} out of range",
                generation
            );
            return (generation << NativeHeapResolver.GenerationShift)
                | (index & NativeHeapResolver.IndexMask);
        }
    }
}
