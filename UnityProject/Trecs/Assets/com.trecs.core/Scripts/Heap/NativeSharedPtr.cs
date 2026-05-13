using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Reference-counted pointer to a shared native (unmanaged) heap allocation. Burst-compatible.
    /// Multiple entities can reference the same data via <see cref="BlobId"/>.
    /// <para>
    /// Allocate via <see cref="HeapAccessor.AllocNativeShared{T}(BlobId)"/>. Open a safety-checked
    /// view with <see cref="HeapAccessor.Read{T}(in NativeSharedPtr{T})"/> on the main thread, or
    /// <see cref="NativeSharedPtrResolver.Read{T}"/> in Burst jobs. <see cref="Clone"/> increments
    /// the reference count; <see cref="Dispose(HeapAccessor)"/> decrements it and frees on zero.
    /// </para>
    /// <para>
    /// Shared native data is immutable by design — any number of readers can resolve the same
    /// blob in parallel without coordination, so the API only exposes read-only access. The
    /// persistent struct stores only (<see cref="PtrHandle"/>, <see cref="BlobId"/>) — 12 bytes,
    /// cheap to copy and store on components. Per-blob <c>AtomicSafetyHandle</c>s live on the
    /// owning heap and are attached to the <see cref="NativeSharedRead{T}"/> wrapper at Open
    /// time so Unity's job-safety walker can detect use-after-free.
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
