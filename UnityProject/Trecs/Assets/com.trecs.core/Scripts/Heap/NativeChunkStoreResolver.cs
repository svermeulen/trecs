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
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NativeChunkStoreEntry
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

        /// <summary>1 if this allocation was routed through the huge-alloc path (dedicated single-slot page).</summary>
        public byte IsHuge;

        public byte _padding;

        /// <summary>Index into the chunk store's page list. Used at free time to return the slot.</summary>
        public int PageId;

        /// <summary>Slot index within the page.</summary>
        public int SlotIndex;

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
    /// Burst-compatible resolver over the chunk store's side table. Cheap to copy by value into
    /// job structs. Resolution is two indexed loads (chunk-directory entry → entry within
    /// chunk) plus a generation/in-use check: no hash lookup, no dictionary probe.
    ///
    /// <para>The side table is stored as a fixed-capacity directory of pointers to
    /// fixed-size chunks of <see cref="NativeChunkStoreEntry"/>. New chunks are appended
    /// when slot indices cross a chunk boundary; existing chunks never move. That stability
    /// is what lets main-thread <c>Alloc</c> materialise new slots concurrent with jobs
    /// reading via this resolver — the list-growth-moves-the-backing-buffer race that a
    /// flat <c>NativeList</c> would have is eliminated by design.</para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NativeChunkStoreResolver
    {
        // Bit layout of the 32-bit PtrHandle.Value encoding the chunk-store identity.
        //   bits  0..23 → side-table index (1..0xFFFFFF; 0 reserved for null)
        //   bits 24..31 → generation       (1..255; 0 reserved for "never allocated")
        internal const int IndexBits = 24;
        internal const uint IndexMask = (1u << IndexBits) - 1u;
        internal const int GenerationShift = IndexBits;
        internal const uint MaxIndex = IndexMask; // 0xFFFFFF == 16,777,215

        // Chunked side-table layout. Each chunk is a fixed-size block of
        // NativeChunkStoreEntry; the directory is a fixed-capacity array of pointers
        // to those chunks (or IntPtr.Zero for chunks not yet materialised).
        internal const int ChunkSizeBits = 10;
        internal const int ChunkSize = 1 << ChunkSizeBits; // 1024 entries per chunk
        internal const int ChunkIndexMask = ChunkSize - 1;

        // Total chunk slots needed to address every possible side-table index.
        internal const int MaxChunkCount = (int)((MaxIndex + 1u) >> ChunkSizeBits); // 16,384

        [NativeDisableContainerSafetyRestriction]
        readonly NativeArray<IntPtr> _chunkDirectory;

        public NativeChunkStoreResolver(NativeArray<IntPtr> chunkDirectory)
        {
            _chunkDirectory = chunkDirectory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeChunkStoreEntry ResolveEntry(PtrHandle handle)
        {
            if (!TryResolveEntry(handle, out var entry))
            {
                throw new TrecsException(
                    $"Could not resolve PtrHandle {handle.Value} through chunk store "
                        + "(null, out of range, freed, or stale generation)"
                );
            }
            return entry;
        }

        /// <summary>
        /// Resolves a handle without throwing on miss. Returns true and populates <paramref name="entry"/>
        /// if the handle is live and the generation matches; returns false otherwise. Use this when the
        /// caller wants to fall back to an alternative lookup (e.g. chained chunk stores) instead of
        /// failing the resolve outright.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryResolveEntry(PtrHandle handle, out NativeChunkStoreEntry entry)
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

            var chunkPtr = (NativeChunkStoreEntry*)_chunkDirectory[chunkIdx].ToPointer();
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint EncodeHandleValue(uint generation, uint index)
        {
            Assert.That(index >= 1 && index <= MaxIndex, "Side-table index {} out of range", index);
            Assert.That(
                generation >= 1 && generation <= 0xFF,
                "Generation {} out of range",
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
