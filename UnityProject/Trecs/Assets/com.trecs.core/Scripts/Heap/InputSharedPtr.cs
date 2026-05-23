using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="InputSharedPtr{T}"/>. Allocation is
    /// gated to input-role accessors; the input heap releases the blob's
    /// refcount when the allocating frame is trimmed.
    /// </summary>
    public static class InputSharedPtr
    {
        public static InputSharedPtr<T> Alloc<T>(HeapAccessor heap, BlobId blobId, T value)
            where T : class
        {
            heap.AssertCanAddInputsSystem();
            return heap.InputSharedHeap.Alloc<T>(heap.FixedFrame, blobId, value);
        }

        public static InputSharedPtr<T> Alloc<T>(WorldAccessor world, BlobId blobId, T value)
            where T : class => Alloc<T>(world.Heap, blobId, value);

        public static InputSharedPtr<T> Acquire<T>(HeapAccessor heap, BlobId blobId)
            where T : class
        {
            heap.AssertCanAddInputsSystem();
            return heap.InputSharedHeap.Acquire<T>(heap.FixedFrame, blobId);
        }

        public static InputSharedPtr<T> Acquire<T>(WorldAccessor world, BlobId blobId)
            where T : class => Acquire<T>(world.Heap, blobId);

        public static bool TryAcquire<T>(
            HeapAccessor heap,
            BlobId blobId,
            out InputSharedPtr<T> ptr
        )
            where T : class
        {
            heap.AssertCanAddInputsSystem();
            return heap.InputSharedHeap.TryAcquire<T>(heap.FixedFrame, blobId, out ptr);
        }

        public static bool TryAcquire<T>(
            WorldAccessor world,
            BlobId blobId,
            out InputSharedPtr<T> ptr
        )
            where T : class => TryAcquire<T>(world.Heap, blobId, out ptr);
    }

    /// <summary>
    /// Reference-counted pointer to a managed shared blob, allocated through
    /// the input pipeline. The object lives in the shared <see cref="BlobCache"/>;
    /// the lifetime of this reference is bound to the allocating input frame.
    /// When the frame is trimmed, the input heap releases its refcount and the
    /// cache evicts the object if no other reference exists.
    ///
    /// <para>Distinct from <see cref="SharedPtr{T}"/>: the type-level split
    /// encodes the lifetime contract — input pointers can only be allocated
    /// from input-role accessors, cannot be manually disposed, and source-gen
    /// rejects them in <c>[Input(MissingInputBehavior.Retain)]</c> fields.</para>
    /// </summary>
    public readonly struct InputSharedPtr<T> : IEquatable<InputSharedPtr<T>>
        where T : class
    {
        public readonly BlobId BlobId;

        internal InputSharedPtr(BlobId blobId)
        {
            BlobId = blobId;
        }

        public readonly bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return BlobId.IsNull; }
        }

        /// <summary>
        /// Resolves the managed value. Throws if the blob has been evicted
        /// (a Retain-style misuse pattern that source-gen catches at compile
        /// time, but checked at runtime too as defense in depth).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Get(HeapAccessor heap)
        {
            TrecsDebugAssert.That(!IsNull, "Cannot Get on a null InputSharedPtr");
            return heap.BlobCache.GetManagedBlob<T>(BlobId, updateAccessTime: true);
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
            return heap.BlobCache.TryGetManagedBlob<T>(BlobId, out value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGet(WorldAccessor world, out T value) =>
            TryGet(world.Heap, out value);

        public readonly bool Equals(InputSharedPtr<T> other) => BlobId.Equals(other.BlobId);

        public override readonly bool Equals(object obj) =>
            obj is InputSharedPtr<T> other && Equals(other);

        public override readonly int GetHashCode() => BlobId.GetHashCode();

        public static bool operator ==(InputSharedPtr<T> l, InputSharedPtr<T> r) => l.Equals(r);

        public static bool operator !=(InputSharedPtr<T> l, InputSharedPtr<T> r) => !l.Equals(r);
    }
}
