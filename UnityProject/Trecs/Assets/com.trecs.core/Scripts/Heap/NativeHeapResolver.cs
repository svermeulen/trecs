using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Side-table entry for a single chunk-store allocation. One entry per <see cref="PtrHandle"/>
    /// the store has handed out; recycled when the handle is freed. The <see cref="Generation"/>
    /// field plus the handle's encoded generation detect stale-handle access after slot reuse.
    ///
    /// <para>Only fields needed by Burst jobs live here (Address, TypeHash, Generation, InUse,
    /// Safety). Main-thread-only bookkeeping (OwnsWholePage, PageId, SlotIndex) lives in the
    /// dense <see cref="NativeHeapEntryPayload"/> list — keeping the entry at 16 bytes improves
    /// cache utilisation during job resolution.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NativeHeapEntry
    {
        /// <summary>Current physical address of the slot. Stable while this entry is live.</summary>
        public IntPtr Address;

        /// <summary>Burst-compatible type hash for the value stored at this slot. 0 means untyped.</summary>
        public int TypeHash;

        /// <summary>
        /// Increments every time this side-table slot transitions (alloc, free).
        /// A handle is valid iff its encoded generation equals this value AND <see cref="InUse"/> is 1.
        /// Wraps around through the byte range, skipping 0 (which means "never allocated").
        /// </summary>
        public byte Generation;

        /// <summary>1 if this slot currently backs a live allocation; 0 otherwise.</summary>
        public byte InUse;

        public byte _pad0;
        public byte _pad1;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        /// <summary>
        /// Per-allocation safety handle. Stable across the lifetime of this allocation:
        /// minted on alloc, released on free, never re-keyed by compaction (compaction
        /// only updates <see cref="Address"/>).
        /// </summary>
        public AtomicSafetyHandle Safety;
#endif
    }

    /// <summary>
    /// Dense-list twin of <see cref="NativeHeapEntry"/> carrying the full set of per-slot
    /// metadata. Main-thread-only bookkeeping fields (<c>OwnsWholePage</c>, <c>PageId</c>,
    /// <c>SlotIndex</c>) live here rather than in the side-table entry, keeping the entry
    /// at 16 bytes for better cache utilisation during job resolution. Also serves as the
    /// on-disk serialization shape — process-local fields (<c>Address</c>,
    /// <c>AtomicSafetyHandle</c>) are excluded, so the byte stream is identical between
    /// editor and release builds.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeHeapEntryPayload
    {
        public int TypeHash;
        public byte Generation;
        public byte InUse;
        public byte OwnsWholePage;
        public byte _padding;
        public int PageId;
        public int SlotIndex;
    }

    /// <summary>
    /// Burst-compatible resolver over the chunk store's side table. Cheap to copy by value into
    /// job structs. Resolution is two indexed loads (chunk-directory entry → entry within
    /// chunk) plus a generation/in-use check: no hash lookup, no dictionary probe.
    ///
    /// <para>The side table is stored as a fixed-capacity directory of pointers to
    /// fixed-size chunks of <see cref="NativeHeapEntry"/>. New chunks are appended
    /// when slot indices cross a chunk boundary; existing chunks never move. That stability
    /// is what lets main-thread <c>Alloc</c> materialise new slots concurrent with jobs
    /// reading via this resolver — the list-growth-moves-the-backing-buffer race that a
    /// flat <c>NativeList</c> would have is eliminated by design.</para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NativeHeapResolver
    {
        // Bit layout of the 32-bit PtrHandle.Value encoding the chunk-store identity.
        //   bits  0..23 → side-table index (1..0xFFFFFF; 0 reserved for null)
        //   bits 24..31 → generation       (1..255; 0 reserved for "never allocated")
        internal const int IndexBits = 24;
        internal const uint IndexMask = (1u << IndexBits) - 1u;
        internal const int GenerationShift = IndexBits;
        internal const uint MaxIndex = IndexMask; // 0xFFFFFF == 16,777,215

        // Chunked side-table layout. Each chunk is a fixed-size block of
        // NativeHeapEntry; the directory is a fixed-capacity array of pointers
        // to those chunks (or IntPtr.Zero for chunks not yet materialised).
        internal const int ChunkSizeBits = 10;
        internal const int ChunkSize = 1 << ChunkSizeBits; // 1024 entries per chunk
        internal const int ChunkIndexMask = ChunkSize - 1;

        // Total chunk slots needed to address every possible side-table index.
        internal const int MaxChunkCount = (int)((MaxIndex + 1u) >> ChunkSizeBits); // 16,384

        [NativeDisableContainerSafetyRestriction]
        readonly NativeArray<IntPtr> _chunkDirectory;

        // 1 if this resolver may be passed to a heap-mutating Write open
        // (TrecsList, NativeUniquePtr); 0 if Read-only. Stamped from the role
        // when WorldAccessor / NativeWorldAccessor hand the resolver out. The
        // raw resolver owned by NativeHeap itself is permissive (1)
        // because it's only consumed by already-gated internal paths.
        readonly byte _canMutateHeap;

        // Internal: only NativeHeap constructs the raw resolver. Role-aware
        // copies for the user-facing surface go through the (in source, bool)
        // overload below.
        internal NativeHeapResolver(NativeArray<IntPtr> chunkDirectory)
        {
            _chunkDirectory = chunkDirectory;
            _canMutateHeap = 1;
        }

        // Role-aware copy: same backing directory, override the mutation bit.
        // Used by WorldAccessor and NativeWorldAccessor to produce a Variable-role
        // resolver that fails fast at Write-open time inside Burst jobs.
        internal NativeHeapResolver(in NativeHeapResolver source, bool canMutateHeap)
        {
            _chunkDirectory = source._chunkDirectory;
            _canMutateHeap = canMutateHeap ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Burst-job-side gate for heap mutation through this resolver. Called from
        /// <see cref="NativeUniquePtr{T}"/>'s <c>Write(in NativeHeapResolver)</c>
        /// and from <see cref="TrecsList{T}"/>'s <c>Write(in NativeHeapResolver)</c>.
        /// The flag is stamped from the originating accessor's role.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertCanMutateHeap()
        {
            TrecsAssert.That(
                _canMutateHeap == 1,
                "Attempted heap mutation (Write) on a heap pointer through a "
                    + "Variable-role NativeHeapResolver. Heap mutation is "
                    + "only allowed from Fixed-role and Unrestricted-role accessors. "
                    + "Read access is always allowed."
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeHeapEntry ResolveEntry(PtrHandle handle)
        {
            TrecsAssert.That(
                TryResolveEntry(handle, out var entry),
                "Could not resolve PtrHandle {0} through chunk store "
                    + "(null, out of range, freed, or stale generation)",
                handle.Value
            );
            return entry;
        }

        /// <summary>
        /// Resolves a handle without throwing on miss. Returns true and populates <paramref name="entry"/>
        /// if the handle is live and the generation matches; returns false otherwise. Use this when the
        /// caller wants to fall back to an alternative lookup (e.g. chained chunk stores) instead of
        /// failing the resolve outright.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryResolveEntry(PtrHandle handle, out NativeHeapEntry entry)
        {
            if (handle.IsNull)
            {
                entry = default;
                return false;
            }

            DecodeHandle(handle, out var index, out var generation);

            var chunkIdx = (int)index >> ChunkSizeBits;
            if (chunkIdx >= _chunkDirectory.Length)
            {
                entry = default;
                return false;
            }

            var chunkPtr = (NativeHeapEntry*)_chunkDirectory[chunkIdx].ToPointer();
            if (chunkPtr == null)
            {
                // Chunk not yet materialised — the handle's index is past the
                // current high-water mark from this resolver's point of view.
                entry = default;
                return false;
            }

            entry = chunkPtr[(int)index & ChunkIndexMask];
            return entry.InUse == 1 && entry.Generation == generation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void* ResolveUnsafePtr(PtrHandle handle)
        {
            return ResolveEntry(handle).Address.ToPointer();
        }

        /// <summary>
        /// Resolves a handle and returns the address of the backing
        /// <see cref="NativeHeapEntry"/> slot in the side table, in addition
        /// to a copy of the entry's current state. Used by Read/Write wrappers
        /// that need a stable pointer to the slot so they can re-check the
        /// slot's <see cref="NativeHeapEntry.Generation"/> on every
        /// access — the shipping-build use-after-dispose guard. Chunks never
        /// move once allocated, so the returned pointer is stable for the
        /// chunk store's lifetime.
        ///
        /// <para>Throws if the handle is null, the chunk hasn't been
        /// materialised yet, the slot's InUse bit is 0, or the slot's
        /// generation doesn't match the handle's encoded generation — same
        /// validity rules as <see cref="ResolveEntry"/>.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe NativeHeapEntry ResolveEntryWithSlotPtr(
            PtrHandle handle,
            out NativeHeapEntry* slotPtr
        )
        {
            TrecsAssert.That(
                TryResolveEntryWithSlotPtr(handle, out var entry, out slotPtr),
                "Could not resolve PtrHandle {0} through chunk store "
                    + "(null, out of range, freed, or stale generation)",
                handle.Value
            );
            return entry;
        }

        /// <summary>
        /// Non-throwing counterpart to <see cref="ResolveEntryWithSlotPtr"/>.
        /// On success returns true and populates both <paramref name="entry"/>
        /// (the entry's value at resolve time) and <paramref name="slotPtr"/>
        /// (the address of that entry in the side-table chunk; stable for the
        /// chunk store's lifetime). On failure both outs are zeroed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryResolveEntryWithSlotPtr(
            PtrHandle handle,
            out NativeHeapEntry entry,
            out NativeHeapEntry* slotPtr
        )
        {
            if (handle.IsNull)
            {
                entry = default;
                slotPtr = null;
                return false;
            }

            DecodeHandle(handle, out var index, out var generation);

            var chunkIdx = (int)index >> ChunkSizeBits;
            if (chunkIdx >= _chunkDirectory.Length)
            {
                entry = default;
                slotPtr = null;
                return false;
            }

            var chunkPtr = (NativeHeapEntry*)_chunkDirectory[chunkIdx].ToPointer();
            if (chunkPtr == null)
            {
                entry = default;
                slotPtr = null;
                return false;
            }

            slotPtr = chunkPtr + ((int)index & ChunkIndexMask);
            entry = *slotPtr;
            return entry.InUse == 1 && entry.Generation == generation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint EncodeHandleValue(uint generation, uint index)
        {
            TrecsDebugAssert.That(
                index >= 1 && index <= MaxIndex,
                "Side-table index {0} out of range",
                index
            );
            TrecsDebugAssert.That(
                generation >= 1 && generation <= 0xFF,
                "Generation {0} out of range",
                generation
            );
            return (generation << GenerationShift) | (index & IndexMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DecodeHandle(PtrHandle handle, out uint index, out byte generation)
        {
            var v = handle.Value;
            index = v & IndexMask;
            generation = (byte)(v >> GenerationShift);
        }
    }
}
