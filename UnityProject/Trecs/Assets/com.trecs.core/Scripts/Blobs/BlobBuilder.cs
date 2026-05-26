using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Builds a relocatable blob: a single contiguous allocation containing a
    /// root <typeparamref name="T"/> plus any trailing data referenced from
    /// the root via <see cref="BlobArray{T}"/> fields. The output is suitable
    /// for <see cref="NativeSharedPtr.AllocTakingOwnership{T}"/> — the heap
    /// takes ownership and frees the allocation when the refcount hits zero.
    ///
    /// <para><b>Why relocatable.</b> Every internal reference inside the blob
    /// is stored as an offset relative to the offset field's own address, not
    /// relative to the root or to any absolute pointer. So <c>memcpy</c>'ing
    /// the entire allocation to a new address — or serializing it to disk and
    /// loading it back — Just Works without any pointer fixup.</para>
    ///
    /// <para><b>Typical usage:</b></para>
    /// <code>
    /// using (var builder = new BlobBuilder(Allocator.Temp))
    /// {
    ///     ref var root = ref builder.ConstructRoot&lt;MyBlob&gt;();
    ///     root.Header = ...;
    ///     var arr = builder.Allocate(ref root.SomeArray, count);
    ///     for (int i = 0; i &lt; count; i++) arr[i] = ...;
    ///     var ptr = builder.Build&lt;MyBlob&gt;(world, blobId);
    /// }
    /// </code>
    ///
    /// <para><b>Build then dispose.</b> <see cref="Build{T}(WorldAccessor, BlobId)"/>
    /// allocates a fresh <see cref="Allocator.Persistent"/> buffer owned by the
    /// heap and copies the blob into it. The builder's working chunks are
    /// independent of that buffer; <see cref="Dispose"/> frees just the
    /// working chunks. The two ownerships don't overlap, so the <c>using</c>
    /// pattern is the cleanest call site.</para>
    /// </summary>
    public unsafe ref struct BlobBuilder
    {
        // One backing allocation in the builder's working state.
        struct BlobChunk
        {
            public byte* Ptr;
            public int Size;
        }

        // Where in the builder's chunks a value lives. Stable across subsequent
        // allocations because chunks themselves never move.
        struct BlobDataRef
        {
            public int ChunkIndex;
            public int Offset;
        }

        // A pending offset to write at Build time. Length != 0 means this is a
        // BlobArray<T> patch (write length at OffsetPtr + 4 too). Length == 0
        // means this is a BlobRef<T> patch (only the 4-byte offset gets
        // written). A length-0 BlobArray<T> never adds a patch — its field
        // stays at default — so the Length == 0 case is unambiguously a
        // BlobRef<T>.
        struct OffsetPtrPatch
        {
            public int* OffsetPtr;
            public BlobDataRef Target;
            public int Length;
        }

        // Used to sort chunks and patches by absolute address so we can walk
        // them in lockstep at Build time — O(n+m) lookup of "which chunk
        // contains each patch's offset field" instead of O(n*m).
        struct SortedIndex : IComparable<SortedIndex>
        {
            public byte* Ptr;
            public int Index;

            public int CompareTo(SortedIndex other) => ((ulong)Ptr).CompareTo((ulong)other.Ptr);
        }

        // All chunks allocated by the builder use this alignment so they
        // concatenate cleanly in the final buffer without breaking element
        // alignment for anything up to AlignOf == 16.
        const int ChunkAlignment = 16;
        const int FinalAlignment = 16;
        const int MaxElementAlignment = 16;
        const int DefaultChunkSize = 65536;

        // Upper bound on chunkSize. Well above any realistic blob the chunked
        // allocator is meant to handle (blobs that large should be streamed or
        // stored differently), and well below int.MaxValue / 16 so the
        // AlignUp(chunkSize, ChunkAlignment) math can't wrap.
        const int MaxChunkSize = 1 << 28; // 256 MiB

        AllocatorManager.AllocatorHandle _allocator;
        NativeList<BlobChunk> _chunks;
        NativeList<OffsetPtrPatch> _patches;
        int _currentChunkIndex;
        int _chunkSize;
        bool _isBuilt;

        public BlobBuilder(
            AllocatorManager.AllocatorHandle allocator,
            int chunkSize = DefaultChunkSize
        )
        {
            TrecsAssert.That(
                chunkSize >= 64 && chunkSize <= MaxChunkSize,
                "BlobBuilder chunkSize {0} outside the supported range [64, {1}]",
                chunkSize,
                MaxChunkSize
            );
            _allocator = allocator;
            _chunks = new NativeList<BlobChunk>(8, _allocator);
            _patches = new NativeList<OffsetPtrPatch>(8, _allocator);
            _chunkSize = AlignUp(chunkSize, ChunkAlignment);
            _currentChunkIndex = -1;
            _isBuilt = false;
        }

        public bool IsCreated => _chunks.IsCreated;

        // Precondition shared by every public method that mutates or
        // finalizes the builder. Catches default(BlobBuilder), use-after-
        // Dispose, and use-after-Build with clear messages instead of letting
        // them surface as confusing NativeList errors.
        void AssertOpen()
        {
            TrecsAssert.That(
                _chunks.IsCreated,
                "BlobBuilder was used uninitialized or after Dispose; construct via `new BlobBuilder(allocator)`."
            );
            TrecsAssert.That(
                !_isBuilt,
                "BlobBuilder has already been built. Create a new builder for each blob."
            );
        }

        /// <summary>
        /// Allocates space for the root <typeparamref name="T"/> at the start
        /// of the blob and returns a writable ref to it. Call once, before any
        /// <see cref="Allocate{T}(ref BlobArray{T}, int)"/>.
        /// </summary>
        public ref T ConstructRoot<T>()
            where T : unmanaged
        {
            AssertOpen();
            // Check `_chunks.Length`, not `_currentChunkIndex`, since
            // `_currentChunkIndex` stays at -1 when the previous allocation
            // overflowed into a standalone chunk (the `size > _chunkSize`
            // branch in AllocateInternal). If sizeof(T) > chunkSize, the
            // root itself takes that branch and leaves _currentChunkIndex
            // alone — without this, a second ConstructRoot call would
            // silently land a second oversized root.
            TrecsAssert.That(
                _chunks.Length == 0,
                "BlobBuilder.ConstructRoot can only be called once, before any other Allocate."
            );
            var dataRef = AllocateInternal(UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>());
            return ref UnsafeUtility.AsRef<T>(ToPointer(dataRef));
        }

        public BlobBuilderArray<T> Allocate<T>(in BlobArray<T> field, int length)
            where T : unmanaged => Allocate(in field, length, UnsafeUtility.AlignOf<T>());

        /// <summary>
        /// Reserves <paramref name="length"/> elements of <typeparamref name="T"/>
        /// in the blob and records a patch so <paramref name="field"/>'s
        /// <c>m_OffsetPtr</c> resolves to the new region at <see cref="Build{T}(WorldAccessor, BlobId)"/>
        /// time. The returned <see cref="BlobBuilderArray{T}"/> is the
        /// write-side view; values written through it land in the blob.
        ///
        /// <para><paramref name="field"/> must be a field of a blob struct
        /// previously allocated by this same builder (either the root from
        /// <see cref="ConstructRoot{T}"/> or a nested struct that itself
        /// lives in builder-owned memory). Passing an unrelated reference —
        /// or worse, an rvalue temp — would record an offset-patch address
        /// outside any of our chunks; the validator below catches both.</para>
        ///
        /// <para><b>Nested arrays.</b> A <see cref="BlobArray{T}"/> field
        /// inside an element of a <see cref="BlobBuilderArray{TElement}"/>
        /// works the same way — the indexer returns <c>ref TElement</c>, and
        /// the nested field's address lives in builder-owned chunk memory
        /// just like a root-level field. The pattern is
        /// <c>builder.Allocate(in regions[i].Polygons, count)</c>.</para>
        /// </summary>
        public BlobBuilderArray<T> Allocate<T>(in BlobArray<T> field, int length, int alignment)
            where T : unmanaged
        {
            AssertOpen();
            TrecsAssert.That(length >= 0, "BlobArray length cannot be negative, got {0}", length);
            TrecsAssert.That(
                math.ispow2(alignment),
                "BlobArray alignment {0} is not a power of two",
                alignment
            );
            TrecsAssert.That(
                alignment <= MaxElementAlignment,
                "BlobArray alignment {0} larger than {1} is not supported",
                alignment,
                MaxElementAlignment
            );
            // Guard against int overflow on the size computation. A
            // multi-billion-byte BlobArray<T> is not a real use case, but a
            // negative `length` value past the assert above would slip
            // through and produce a negative `size` that confuses
            // EnsureRoom/AllocateInternal.
            var elementSize = UnsafeUtility.SizeOf<T>();
            TrecsAssert.That(
                (long)elementSize * length <= int.MaxValue,
                "BlobArray<T> total size overflows int: {0} elements * {1} bytes",
                length,
                elementSize
            );

            // Length-0 arrays carry the default (m_OffsetPtr=0, m_Length=0).
            // The indexer is unreachable when m_Length is 0, so no allocation
            // or patch is needed — we just leave the field as-is.
            if (length == 0)
            {
                return new BlobBuilderArray<T>(null, 0);
            }

            // Snapshot the address of the field's m_OffsetPtr. This lives
            // inside one of our chunks (ValidateAllocation asserts that), and
            // chunks have stable addresses for their lifetime, so this absolute
            // pointer stays valid through subsequent allocations and until
            // Build / Dispose. Unsafe.AsRef strips `in`-readonly so we can
            // AddressOf it; we never write through that ref ourselves.
            var offsetPtr = (int*)
                UnsafeUtility.AddressOf(
                    ref UnsafeUtility.As<BlobArray<T>, int>(ref Unsafe.AsRef(in field))
                );
            ValidateAllocation(offsetPtr);

            var dataRef = AllocateInternal(elementSize * length, alignment);

            _patches.Add(
                new OffsetPtrPatch
                {
                    OffsetPtr = offsetPtr,
                    Target = dataRef,
                    Length = length,
                }
            );

            return new BlobBuilderArray<T>(ToPointer(dataRef), length);
        }

        /// <summary>
        /// Reserves space for a single <typeparamref name="T"/> in the blob,
        /// records a patch so <paramref name="field"/>'s <c>m_OffsetPtr</c>
        /// resolves to the new region at <see cref="Build{T}(WorldAccessor, BlobId)"/>
        /// time, and returns a writable ref so the caller can fill the value.
        ///
        /// <para>Single-value counterpart to
        /// <see cref="Allocate{T}(in BlobArray{T}, int)"/>. Use when the
        /// enclosing blob needs to reference a single nested struct rather
        /// than an array — polymorphic variant pointers, optional sub-
        /// structures, and in-blob cross-references are the typical cases.
        /// For an optional sub-structure, leave the field at default
        /// (<see cref="BlobRef{T}.IsValid"/> returns <c>false</c>); skip the
        /// <c>Allocate</c> call to mark it absent.</para>
        ///
        /// <para>The same call-site constraint as the array overload
        /// applies: <paramref name="field"/> must be a field of a blob struct
        /// previously allocated by this builder. <see cref="ValidateAllocation"/>
        /// catches rvalues / unrelated references.</para>
        /// </summary>
        public ref T Allocate<T>(in BlobRef<T> field)
            where T : unmanaged => ref Allocate(in field, UnsafeUtility.AlignOf<T>());

        public ref T Allocate<T>(in BlobRef<T> field, int alignment)
            where T : unmanaged
        {
            AssertOpen();
            TrecsAssert.That(
                math.ispow2(alignment),
                "BlobRef alignment {0} is not a power of two",
                alignment
            );
            TrecsAssert.That(
                alignment <= MaxElementAlignment,
                "BlobRef alignment {0} larger than {1} is not supported",
                alignment,
                MaxElementAlignment
            );

            // Snapshot &m_OffsetPtr inside the enclosing chunk; same idiom as
            // the array overload, just with no length to record. The patch's
            // Length == 0 is what tells BuildNativeBlobAllocation to write
            // only the 4-byte offset (no trailing length field).
            var offsetPtr = (int*)
                UnsafeUtility.AddressOf(
                    ref UnsafeUtility.As<BlobRef<T>, int>(ref Unsafe.AsRef(in field))
                );
            ValidateAllocation(offsetPtr);

            var dataRef = AllocateInternal(UnsafeUtility.SizeOf<T>(), alignment);

            _patches.Add(
                new OffsetPtrPatch
                {
                    OffsetPtr = offsetPtr,
                    Target = dataRef,
                    Length = 0,
                }
            );

            return ref UnsafeUtility.AsRef<T>(ToPointer(dataRef));
        }

        /// <summary>
        /// Finalizes the blob into a single Allocator.Persistent allocation
        /// owned by the heap, returning a fresh <see cref="NativeSharedPtr{T}"/>
        /// for the seeded entry. Convenience over
        /// <see cref="BuildNativeBlobAllocation"/> +
        /// <see cref="NativeSharedPtr.AllocTakingOwnership{T}(WorldAccessor, BlobId, NativeBlobAllocation)"/>.
        /// </summary>
        public NativeSharedPtr<T> Build<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged
        {
            var alloc = BuildNativeBlobAllocation();
            // Hand-off must be exception-safe: BuildNativeBlobAllocation has
            // already produced the persistent buffer, so any throw from
            // AllocTakingOwnership (heap-state asserts, duplicate-id checks,
            // generic type-mismatch in the underlying store) would otherwise
            // drop the only reference to it and leak.
            try
            {
                return NativeSharedPtr.AllocTakingOwnership<T>(world, blobId, alloc);
            }
            catch
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    (void*)alloc.Ptr,
                    alloc.AllocSize,
                    alloc.Alignment,
                    items: 1
                );
                throw;
            }
        }

        /// <summary>
        /// Low-level finalize. Copies every chunk into a single fresh
        /// <see cref="Allocator.Persistent"/> allocation, resolves every
        /// offset-pointer patch, and returns the resulting <see cref="NativeBlobAllocation"/>.
        /// The caller is responsible for handing it to a taking-ownership API
        /// (<see cref="NativeSharedPtr.AllocTakingOwnership{T}(WorldAccessor, BlobId, NativeBlobAllocation)"/>
        /// or <see cref="NativeUniquePtr.AllocTakingOwnership{T}"/>) — whichever
        /// takes it on will free it via <c>AllocatorManager.Free</c> at end-of-life.
        /// </summary>
        public NativeBlobAllocation BuildNativeBlobAllocation()
        {
            AssertOpen();
            // Check `_chunks.Length`, not `_currentChunkIndex`, since
            // `_currentChunkIndex` stays at -1 when every allocation took
            // the overflow path (size > _chunkSize) in AllocateInternal.
            // A blob built entirely from oversized allocations is valid —
            // each standalone chunk holds one allocation aligned to
            // ChunkAlignment, so they concatenate cleanly without any
            // trailing-pad step.
            TrecsAssert.That(
                _chunks.Length > 0,
                "BlobBuilder has no allocations; did you forget to call ConstructRoot?"
            );

            // Pad the last *regular* chunk up to 16-byte alignment so chunks
            // concatenate cleanly in the final buffer (every chunk's tail
            // boundary is a valid alignment boundary for the next chunk's
            // contents). Standalone overflow chunks are already
            // ChunkAlignment-sized at allocation time and need no pad.
            if (_currentChunkIndex != -1)
            {
                AlignChunkTail(_currentChunkIndex);
            }

            // Position-in-final-buffer for each chunk, in *original* (insertion)
            // order. offsets[_chunks.Length] is the total size.
            var offsets = new NativeArray<int>(_chunks.Length + 1, Allocator.Temp);
            var sortedChunks = new NativeArray<SortedIndex>(_chunks.Length, Allocator.Temp);
            var sortedPatches = new NativeArray<SortedIndex>(_patches.Length, Allocator.Temp);
            try
            {
                offsets[0] = 0;
                for (int i = 0; i < _chunks.Length; ++i)
                {
                    offsets[i + 1] = offsets[i] + _chunks[i].Size;
                    sortedChunks[i] = new SortedIndex { Ptr = _chunks[i].Ptr, Index = i };
                }
                int totalSize = offsets[_chunks.Length];

                sortedChunks.Sort();
                for (int i = 0; i < _patches.Length; ++i)
                {
                    sortedPatches[i] = new SortedIndex
                    {
                        Ptr = (byte*)_patches[i].OffsetPtr,
                        Index = i,
                    };
                }
                sortedPatches.Sort();

                // The destination allocation.
                var buffer = (byte*)
                    AllocatorManager.Allocate(
                        Allocator.Persistent,
                        totalSize,
                        FinalAlignment,
                        items: 1
                    );

                for (int i = 0; i < _chunks.Length; ++i)
                {
                    UnsafeUtility.MemCpy(buffer + offsets[i], _chunks[i].Ptr, _chunks[i].Size);
                }

                // Walk patches in address-sorted order; advance through
                // sortedChunks in lockstep to find which chunk each patch's
                // offset-field address belongs to.
                if (_patches.Length > 0)
                {
                    int iChunk = 0;
                    var chunkStart = _chunks[sortedChunks[0].Index].Ptr;
                    var chunkEnd = chunkStart + _chunks[sortedChunks[0].Index].Size;

                    for (int i = 0; i < _patches.Length; ++i)
                    {
                        int patchIndex = sortedPatches[i].Index;
                        var offsetPtr = (int*)sortedPatches[i].Ptr;

                        while ((byte*)offsetPtr >= chunkEnd)
                        {
                            ++iChunk;
                            chunkStart = _chunks[sortedChunks[iChunk].Index].Ptr;
                            chunkEnd = chunkStart + _chunks[sortedChunks[iChunk].Index].Size;
                        }

                        var patch = _patches[patchIndex];
                        int offsetPtrInBuffer =
                            offsets[sortedChunks[iChunk].Index]
                            + (int)((byte*)offsetPtr - chunkStart);
                        int targetInBuffer = offsets[patch.Target.ChunkIndex] + patch.Target.Offset;

                        *(int*)(buffer + offsetPtrInBuffer) = targetInBuffer - offsetPtrInBuffer;
                        if (patch.Length != 0)
                        {
                            *(int*)(buffer + offsetPtrInBuffer + 4) = patch.Length;
                        }
                    }
                }

                _isBuilt = true;
                return new NativeBlobAllocation((IntPtr)buffer, totalSize, FinalAlignment);
            }
            finally
            {
                offsets.Dispose();
                sortedChunks.Dispose();
                sortedPatches.Dispose();
            }
        }

        public void Dispose()
        {
            if (!_chunks.IsCreated)
            {
                return;
            }
            for (int i = 0; i < _chunks.Length; ++i)
            {
                AllocatorManager.Free(
                    _allocator,
                    _chunks[i].Ptr,
                    _chunks[i].Size,
                    ChunkAlignment,
                    items: 1
                );
            }
            _chunks.Dispose();
            _patches.Dispose();
        }

        // ─── Internal allocation machinery ────────────────────────────

        BlobDataRef AllocateInternal(int size, int alignment)
        {
            if (size > _chunkSize)
            {
                // Anything larger than a chunk gets its own standalone chunk.
                // Aligned up to ChunkAlignment so Free's (size, alignment)
                // matches what we allocate with.
                int allocSize = AlignUp(size, ChunkAlignment);
                int chunkIndex = _chunks.Length;
                var mem = (byte*)
                    AllocatorManager.Allocate(_allocator, allocSize, ChunkAlignment, items: 1);
                UnsafeUtility.MemClear(mem, allocSize);
                _chunks.Add(new BlobChunk { Ptr = mem, Size = allocSize });
                return new BlobDataRef { ChunkIndex = chunkIndex, Offset = 0 };
            }

            var chunk = EnsureRoom(size, alignment);
            int offset = chunk.Size;
            UnsafeUtility.MemClear(chunk.Ptr + offset, size);
            chunk.Size += size;
            _chunks[_currentChunkIndex] = chunk;
            return new BlobDataRef { ChunkIndex = _currentChunkIndex, Offset = offset };
        }

        BlobChunk EnsureRoom(int size, int alignment)
        {
            if (_currentChunkIndex == -1)
            {
                return NewChunk();
            }

            var chunk = _chunks[_currentChunkIndex];
            int alignedUsed = AlignUp(chunk.Size, alignment);
            if (alignedUsed + size > _chunkSize)
            {
                return NewChunk();
            }

            // Pad to alignment before the new bytes land.
            UnsafeUtility.MemClear(chunk.Ptr + chunk.Size, alignedUsed - chunk.Size);
            chunk.Size = alignedUsed;
            _chunks[_currentChunkIndex] = chunk;
            return chunk;
        }

        BlobChunk NewChunk()
        {
            // Round the previous chunk's used-size up to 16 so concatenation
            // in the final buffer keeps every chunk's interior 16-aligned.
            if (_currentChunkIndex != -1)
            {
                AlignChunkTail(_currentChunkIndex);
            }
            _currentChunkIndex = _chunks.Length;
            var mem = (byte*)
                AllocatorManager.Allocate(_allocator, _chunkSize, ChunkAlignment, items: 1);
            var chunk = new BlobChunk { Ptr = mem, Size = 0 };
            _chunks.Add(chunk);
            return chunk;
        }

        void AlignChunkTail(int chunkIndex)
        {
            var chunk = _chunks[chunkIndex];
            int oldSize = chunk.Size;
            chunk.Size = AlignUp(chunk.Size, ChunkAlignment);
            _chunks[chunkIndex] = chunk;
            UnsafeUtility.MemClear(chunk.Ptr + oldSize, chunk.Size - oldSize);
        }

        void* ToPointer(BlobDataRef dataRef) => _chunks[dataRef.ChunkIndex].Ptr + dataRef.Offset;

        // Always-on (not conditional on safety-checks). The `in BlobArray<T>`
        // parameter on Allocate accepts rvalues — e.g. someone passing
        // `default(BlobArray<T>)` — which would compile but record an offset-
        // patch address into a stack temp that's gone by Build time. The walk
        // is O(chunks) ≈ tiny in practice (most blobs use 1-3 chunks).
        void ValidateAllocation(void* address)
        {
            // Walk backwards — the most-recent chunk is most likely to contain
            // the address.
            for (int i = _chunks.Length - 1; i >= 0; --i)
            {
                var chunk = _chunks[i];
                if (address >= chunk.Ptr && address < chunk.Ptr + chunk.Size)
                {
                    return;
                }
            }
            throw new InvalidOperationException(
                "The BlobArray<T> reference passed to BlobBuilder.Allocate was not "
                    + "produced by this builder. The most common cause is passing a "
                    + "BlobArray<T> rvalue (e.g. `default(BlobArray<T>)`) instead of a "
                    + "field of a struct previously allocated by this builder."
            );
        }

        static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
    }
}
