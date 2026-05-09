using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Reference-counted pointer to a shared managed (class) heap allocation. Multiple entities
    /// can hold a <see cref="SharedPtr{T}"/> referencing the same underlying object, identified
    /// by a <see cref="BlobId"/>. Allocate via <see cref="HeapAccessor.AllocShared{T}"/>.
    /// <para>
    /// Resolve the value with <see cref="Get(HeapAccessor)"/> or <see cref="Get(WorldAccessor)"/>.
    /// Cloning increments the reference count; disposing decrements it and frees when zero.
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

        public SharedPtr(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            Id = blobId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(HeapAccessor heap)
        {
            Assert.That(!IsNull);

            if (heap.SharedHeap.TryGetBlobDirect<T>(Id, Handle, out var result))
            {
                return result;
            }

            if (
                heap.FrameScopedSharedHeap.TryResolveValue<T>(
                    heap.FixedFrame,
                    Handle.Value,
                    out result
                )
            )
            {
                return result;
            }

            throw Assert.CreateException(
                "Failed to resolve SharedPtr with id {} and handle {}",
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

            if (
                heap.FrameScopedSharedHeap.TryResolveValue<T>(
                    heap.FixedFrame,
                    Handle.Value,
                    out value
                )
            )
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

            if (heap.SharedHeap.ContainsBlobDirect(Id, Handle))
            {
                return true;
            }

            return heap.FrameScopedSharedHeap.ContainsEntry(Handle.Value);
        }

        public readonly bool CanGet(WorldAccessor world) => CanGet(world.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SharedPtr<T> Clone(HeapAccessor heap)
        {
            if (IsNull)
            {
                return default;
            }

            if (heap.SharedHeap.TryClone<T>(Handle, out var result))
            {
                return result;
            }

            // Frame-scoped: clone into persistent heap
            var blobId = heap.FrameScopedSharedHeap.GetBlobId(heap.FixedFrame, Handle.Value);
            return heap.SharedHeap.GetBlob<T>(blobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SharedPtr<T> Clone(WorldAccessor world) => Clone(world.Heap);

        public readonly BlobId GetBlobId(HeapAccessor heap)
        {
            if (heap.SharedHeap.ContainsBlobDirect(Id, Handle))
            {
                return Id;
            }

            Assert.That(!IsNull);
            return heap.FrameScopedSharedHeap.GetBlobId(heap.FixedFrame, Handle.Value);
        }

        public readonly BlobId GetBlobId(WorldAccessor world) => GetBlobId(world.Heap);

        public readonly void Dispose(HeapAccessor heap)
        {
            Assert.That(
                !heap.FrameScopedSharedHeap.ContainsEntry(Handle.Value),
                "Frame-scoped SharedPtr must not be manually disposed"
            );
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
