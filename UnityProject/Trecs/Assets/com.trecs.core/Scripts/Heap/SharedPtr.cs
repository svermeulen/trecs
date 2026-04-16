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
        public T Get(HeapAccessor heap)
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
        public T Get(WorldAccessor ecs) => Get(ecs.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(HeapAccessor heap, out T value)
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
        public bool TryGet(WorldAccessor ecs, out T value) => TryGet(ecs.Heap, out value);

        public bool CanGet(HeapAccessor heap)
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

        public bool CanGet(WorldAccessor ecs) => CanGet(ecs.Heap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SharedPtr<T> Clone(HeapAccessor heap)
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
        public SharedPtr<T> Clone(WorldAccessor ecs) => Clone(ecs.Heap);

        public BlobId GetBlobId(HeapAccessor heap)
        {
            if (heap.SharedHeap.ContainsBlobDirect(Id, Handle))
            {
                return Id;
            }

            Assert.That(!IsNull);
            return heap.FrameScopedSharedHeap.GetBlobId(heap.FixedFrame, Handle.Value);
        }

        public BlobId GetBlobId(WorldAccessor ecs) => GetBlobId(ecs.Heap);

        public readonly void Dispose(HeapAccessor heap)
        {
            Assert.That(
                !heap.FrameScopedSharedHeap.ContainsEntry(Handle.Value),
                "Frame-scoped SharedPtr must not be manually disposed"
            );
            heap.SharedHeap.DisposeHandle(Handle);
        }

        public readonly void Dispose(WorldAccessor ecs) => Dispose(ecs.Heap);

        public readonly bool IsNull
        {
            get { return Id.IsNull; }
        }

        public bool Equals(SharedPtr<T> other)
        {
            return Handle.Equals(other.Handle) && Id.Equals(other.Id);
        }

        public override bool Equals(object obj)
        {
            return obj is SharedPtr<T> other && Equals(other);
        }

        public override int GetHashCode()
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
