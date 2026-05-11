using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Reference-counted pointer to a shared native (unmanaged) heap allocation. Burst-compatible.
    /// Multiple entities can reference the same data via <see cref="BlobId"/>. Resolve to a
    /// <c>ref readonly T</c> via <see cref="Get(in NativeSharedPtrResolver)"/> in jobs or
    /// <see cref="Get(HeapAccessor)"/> on the main thread. Shared native data is immutable by
    /// design — any number of readers can resolve the same blob in parallel without coordination,
    /// so the API only exposes read-only access.
    /// <para>
    /// Allocate via <see cref="HeapAccessor.AllocNativeShared{T}"/>. Cloning increments the
    /// reference count; disposing decrements it and frees when zero.
    /// </para>
    /// <para>
    /// Public verb set: <c>GetUnsafePtr</c>, <c>Get</c>, <c>Clone</c>, <c>Dispose</c>,
    /// <c>IsNull</c>. <c>Get</c> / <c>GetUnsafePtr</c> have job-side overloads
    /// (<see cref="NativeSharedPtrResolver"/> / <see cref="NativeWorldAccessor"/>) and main-thread
    /// overloads (<see cref="HeapAccessor"/> / <see cref="WorldAccessor"/>). <c>Clone</c> and
    /// <c>Dispose</c> are main-thread-only by design — they mutate ref-count bookkeeping in the
    /// shared heap, which isn't accessible from a Burst job. There is no public <c>TryGet</c> /
    /// <c>CanGet</c>: a live <see cref="NativeSharedPtr{T}"/> is expected to resolve, since
    /// disposal mirrors managed <see cref="SharedPtr{T}"/> — if you're worried about lifetimes,
    /// you're holding a stale ptr.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Packed with <c>LayoutKind.Sequential, Pack = 1</c> to minimize component size (12 bytes).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly unsafe struct NativeSharedPtr<T> : IEquatable<NativeSharedPtr<T>>
        where T : unmanaged
    {
        public readonly PtrHandle Handle;
        public readonly BlobId BlobId;

        public NativeSharedPtr(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            BlobId = blobId;
        }

        public readonly unsafe void* GetUnsafePtr(in NativeSharedPtrResolver nativePtrResolver)
        {
            return nativePtrResolver.ResolveUnsafePtr<T>(BlobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe void* GetUnsafePtr(in NativeWorldAccessor accessor)
        {
            return GetUnsafePtr(accessor.SharedPtrResolver);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe void* GetUnsafePtr(HeapAccessor heap)
        {
            return heap.ResolveUnsafePtr<T>(BlobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe void* GetUnsafePtr(WorldAccessor world)
        {
            return GetUnsafePtr(world.Heap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(in NativeSharedPtrResolver nativePtrResolver)
        {
            return ref UnsafeUtility.AsRef<T>(GetUnsafePtr(nativePtrResolver));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(in NativeWorldAccessor accessor)
        {
            return ref UnsafeUtility.AsRef<T>(GetUnsafePtr(accessor.SharedPtrResolver));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(HeapAccessor heap)
        {
            return ref UnsafeUtility.AsRef<T>(GetUnsafePtr(heap));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(WorldAccessor world)
        {
            return ref Get(world.Heap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeSharedPtr<T> Clone(HeapAccessor heap)
        {
            if (IsNull)
            {
                return default;
            }

            if (heap.NativeSharedHeap.TryClone<T>(Handle, out var result))
            {
                return result;
            }

            // Frame-scoped: clone into persistent heap
            var blobId = heap.FrameScopedNativeSharedHeap.GetBlobId(heap.FixedFrame, Handle.Value);
            return heap.NativeSharedHeap.GetBlob<T>(blobId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeSharedPtr<T> Clone(WorldAccessor world) => Clone(world.Heap);

        public readonly void Dispose(HeapAccessor heap)
        {
            Assert.That(
                !heap.FrameScopedNativeSharedHeap.ContainsEntry(Handle.Value),
                "Frame-scoped NativeSharedPtr must not be manually disposed"
            );
            heap.NativeSharedHeap.DisposeHandle(Handle);
        }

        public readonly void Dispose(WorldAccessor world) => Dispose(world.Heap);

        public readonly bool IsNull
        {
            get { return BlobId.IsNull; }
        }

        /// <remarks>
        /// Equality compares both <see cref="Handle"/> and <see cref="BlobId"/>. Two
        /// <see cref="NativeSharedPtr{T}"/> instances pointing at the same underlying blob
        /// (same <see cref="BlobId"/>) but holding different <see cref="PtrHandle"/>s —
        /// e.g. one cloned from the other — are <i>not</i> equal here, since each handle
        /// represents a distinct reference-count slot. Compare <see cref="BlobId"/> directly
        /// when "do these point at the same blob?" is the actual question.
        /// </remarks>
        public readonly bool Equals(NativeSharedPtr<T> other)
        {
            return Handle.Equals(other.Handle) && BlobId.Equals(other.BlobId);
        }

        public override readonly bool Equals(object obj)
        {
            return obj is NativeSharedPtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return unchecked((int)math.hash(new int2(Handle.GetHashCode(), BlobId.GetHashCode())));
        }

        public static bool operator ==(NativeSharedPtr<T> left, NativeSharedPtr<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(NativeSharedPtr<T> left, NativeSharedPtr<T> right)
        {
            return !left.Equals(right);
        }
    }
}
