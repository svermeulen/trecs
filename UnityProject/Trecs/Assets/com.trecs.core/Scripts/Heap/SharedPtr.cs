using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="SharedPtr{T}"/>. Per-instance operations
    /// (<c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Clone</c>, <c>GetBlobId</c>, <c>Dispose</c>)
    /// live on the struct itself.
    /// </summary>
    public static class SharedPtr
    {
        /// <summary>
        /// Allocates a shared blob under an explicit, caller-supplied
        /// <see cref="BlobId"/>. Use a <see cref="BlobId"/> factory
        /// (<see cref="BlobIdGenerator.FromKey"/>, <see cref="BlobId.FromGuid"/>,
        /// <see cref="BlobId.FromBytes"/>, or the content-hash extension in
        /// <c>Trecs</c>) to obtain one — persistent allocations
        /// always carry caller-chosen identity.
        /// </summary>
        public static SharedPtr<T> Alloc<T>(HeapAccessor heap, BlobId blobId, T value)
            where T : class
        {
            heap.AssertCanMutateHeap();
            return heap.SharedHeap.CreateBlob<T>(blobId, value);
        }

        /// <summary>
        /// Returns a fresh reference-counted handle to the existing blob at
        /// <paramref name="blobId"/>, throwing if no such blob exists. This is
        /// the lookup-only counterpart to <see cref="Alloc{T}(HeapAccessor, BlobId, T)"/>
        /// — it does not allocate new memory, just acquires another refcount
        /// slot on data that has already been seeded.
        /// </summary>
        public static SharedPtr<T> Acquire<T>(HeapAccessor heap, BlobId blobId)
            where T : class
        {
            heap.AssertCanMutateHeap();
            return heap.SharedHeap.GetBlob<T>(blobId);
        }

        public static SharedPtr<T> Alloc<T>(WorldAccessor world, BlobId blobId, T value)
            where T : class => Alloc<T>(world.Heap, blobId, value);

        public static SharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : class => Acquire<T>(world.Heap, blobId);

        /// <summary>
        /// Returns true and the cached blob if one exists at <paramref name="blobId"/>; otherwise false.
        /// </summary>
        public static bool TryGet<T>(HeapAccessor heap, BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            heap.AssertCanMutateHeap();
            return heap.SharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        public static bool TryGet<T>(WorldAccessor world, BlobId blobId, out SharedPtr<T> ptr)
            where T : class => TryGet<T>(world.Heap, blobId, out ptr);

        /// <summary>
        /// Returns the existing shared blob at <paramref name="blobId"/> if cached,
        /// otherwise calls <paramref name="factory"/> and stores the result. The factory
        /// is only invoked on cache miss.
        /// See <see cref="NativeSharedPtr.GetOrAlloc{T}(HeapAccessor, BlobId, Func{T})"/> for
        /// how to keep <paramref name="factory"/> allocation-free.
        /// </summary>
        public static SharedPtr<T> GetOrAlloc<T>(HeapAccessor heap, BlobId blobId, Func<T> factory)
            where T : class
        {
            heap.AssertCanMutateHeap();
            if (heap.SharedHeap.TryGetBlob<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return heap.SharedHeap.CreateBlob<T>(blobId, factory());
        }

        public static SharedPtr<T> GetOrAlloc<T>(
            WorldAccessor world,
            BlobId blobId,
            Func<T> factory
        )
            where T : class => GetOrAlloc<T>(world.Heap, blobId, factory);
    }

    /// <summary>
    /// Reference-counted pointer to a shared managed (class) heap allocation. Multiple entities
    /// can hold a <see cref="SharedPtr{T}"/> referencing the same underlying object, identified
    /// by a <see cref="BlobId"/>. Seed via <see cref="SharedPtr.Alloc{T}(HeapAccessor, BlobId, T)"/>;
    /// look up an already-seeded blob via <see cref="SharedPtr.Acquire{T}(HeapAccessor, BlobId)"/>.
    /// <para>
    /// Resolve the value with <see cref="Get(HeapAccessor)"/> or <see cref="Get(WorldAccessor)"/>.
    /// Cloning increments the reference count; disposing decrements it and frees when zero.
    /// </para>
    /// <para>
    /// <b>T must be marked <see cref="ImmutableAttribute"/></b> (or be one of the implicitly-
    /// allowed types like <c>string</c>) — enforced by the TRECS125 analyzer. Managed shared
    /// blobs live in the BlobCache, which is not snapshotted alongside game-state snapshots,
    /// so any post-Alloc mutation silently desyncs determinism. See <see cref="ImmutableAttribute"/>
    /// for the full contract and TRECS126 for what gets validated.
    /// </para>
    /// <para>
    /// Public verb set: <c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Clone</c>, <c>GetBlobId</c>,
    /// <c>Dispose</c>, <c>IsNull</c>. The struct is itself <c>readonly</c>; all instance methods
    /// are marked <c>readonly</c> to match the rest of the pointer family — none of them mutate
    /// the ptr struct (heap state mutates instead).
    /// </para>
    /// </summary>
    public readonly struct SharedPtr<T> : IEquatable<SharedPtr<T>>
        where T : class
    {
        public readonly PtrHandle Handle;
        public readonly BlobId Id;

        // Internal so external code can't fabricate a handle from arbitrary values.
        // Allocation goes through SharedPtr.Alloc / Clone; deserialization paths
        // live in InternalsVisibleTo-allowed assemblies.
        internal SharedPtr(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            Id = blobId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(HeapAccessor heap)
        {
            TrecsDebugAssert.That(!IsNull);

            if (heap.SharedHeap.TryGetBlobDirect<T>(Id, Handle, out var result))
            {
                return result;
            }

            throw TrecsDebugAssert.CreateException(
                "Failed to resolve SharedPtr with id {0} and handle {1}",
                Id,
                Handle
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(WorldAccessor world) => Get(world.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(HeapAccessor heap, out T value)
        {
            if (IsNull)
            {
                value = null;
                return false;
            }

            if (heap.SharedHeap.TryGetBlobDirect<T>(Id, Handle, out value))
            {
                return true;
            }

            value = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor world, out T value) =>
            TryGet(world.Heap, out value);

        public readonly bool CanGet(HeapAccessor heap)
        {
            if (IsNull)
            {
                return false;
            }

            return heap.SharedHeap.ContainsBlobDirect(Id, Handle);
        }

        public readonly bool CanGet(WorldAccessor world) => CanGet(world.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SharedPtr<T> Clone(HeapAccessor heap)
        {
            heap.AssertCanMutateHeap();
            if (IsNull)
            {
                return default;
            }
            if (!heap.SharedHeap.TryClone<T>(Handle, out var result))
            {
                throw TrecsDebugAssert.CreateException(
                    "Failed to clone SharedPtr with id {0} and handle {1}",
                    Id,
                    Handle
                );
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SharedPtr<T> Clone(WorldAccessor world) => Clone(world.Heap);

        public readonly BlobId GetBlobId(HeapAccessor heap)
        {
            TrecsDebugAssert.That(!IsNull);
            return Id;
        }

        public readonly BlobId GetBlobId(WorldAccessor world) => GetBlobId(world.Heap);

        public readonly void Dispose(HeapAccessor heap)
        {
            heap.AssertCanMutateHeap();
            heap.SharedHeap.DisposeHandle(Handle);
        }

        public readonly void Dispose(WorldAccessor world) => Dispose(world.Heap);

        public readonly bool IsNull
        {
            get { return Id.IsNull; }
        }

        /// <remarks>
        /// Equality compares both <see cref="Handle"/> and <see cref="Id"/>. Two
        /// <see cref="SharedPtr{T}"/> instances pointing at the same underlying blob
        /// (same <see cref="BlobId"/>) but holding different <see cref="PtrHandle"/>s —
        /// e.g. one cloned from the other — are <i>not</i> equal here, since each handle
        /// represents a distinct reference-count slot. Compare <see cref="Id"/> directly
        /// when "do these point at the same blob?" is the actual question.
        /// </remarks>
        public readonly bool Equals(SharedPtr<T> other)
        {
            return Handle.Equals(other.Handle) && Id.Equals(other.Id);
        }

        public override readonly bool Equals(object obj)
        {
            return obj is SharedPtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return unchecked((int)math.hash(new int2(Handle.GetHashCode(), Id.GetHashCode())));
        }

        public static bool operator ==(SharedPtr<T> left, SharedPtr<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(SharedPtr<T> left, SharedPtr<T> right)
        {
            return !left.Equals(right);
        }
    }
}
