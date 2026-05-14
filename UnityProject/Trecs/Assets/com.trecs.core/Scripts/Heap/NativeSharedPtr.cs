using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="NativeSharedPtr{T}"/>. Per-instance operations
    /// (<c>Read</c>, <c>Clone</c>, <c>Dispose</c>) live on the struct itself.
    /// </summary>
    public static class NativeSharedPtr
    {
        public static NativeSharedPtr<T> Alloc<T>(HeapAccessor heap, BlobId blobId)
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeSharedHeap.GetBlob<T>(blobId);
        }

        public static NativeSharedPtr<T> Alloc<T>(HeapAccessor heap, BlobId blobId, in T value)
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeSharedHeap.CreateBlob<T>(blobId, in value);
        }

        public static NativeSharedPtr<T> Alloc<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged => Alloc<T>(world.Heap, blobId);

        public static NativeSharedPtr<T> Alloc<T>(WorldAccessor world, BlobId blobId, in T value)
            where T : unmanaged => Alloc<T>(world.Heap, blobId, in value);

        /// <summary>
        /// Returns true and the cached blob if one exists at <paramref name="blobId"/>; otherwise false.
        /// </summary>
        public static bool TryGet<T>(HeapAccessor heap, BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeSharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        public static bool TryGet<T>(WorldAccessor world, BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged => TryGet<T>(world.Heap, blobId, out ptr);

        /// <summary>
        /// Takes ownership of an existing native allocation with an explicit BlobId.
        /// See <see cref="NativeUniquePtr.AllocTakingOwnership{T}"/> for the ownership contract.
        /// </summary>
        public static NativeSharedPtr<T> AllocTakingOwnership<T>(
            HeapAccessor heap,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, alloc);
        }

        public static NativeSharedPtr<T> AllocTakingOwnership<T>(
            WorldAccessor world,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged => AllocTakingOwnership<T>(world.Heap, blobId, alloc);

        /// <summary>
        /// Returns the existing native-shared blob at <paramref name="blobId"/> if cached,
        /// otherwise calls <paramref name="factory"/> and stores the result. The factory
        /// is only invoked on cache miss.
        /// <para>
        /// To stay allocation-free, pass <paramref name="factory"/> as either a
        /// <c>static</c> method group (<c>BuildIt</c>), a <c>static</c> lambda
        /// (<c>static () =&gt; …</c>, C# 9+), or a cached <c>static readonly Func&lt;T&gt;</c>
        /// field. Plain lambdas that capture local state allocate a closure on every call.
        /// </para>
        /// </summary>
        public static NativeSharedPtr<T> GetOrAlloc<T>(
            HeapAccessor heap,
            BlobId blobId,
            Func<T> factory
        )
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            if (heap.NativeSharedHeap.TryGetBlob<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return heap.NativeSharedHeap.CreateBlob<T>(blobId, factory());
        }

        public static NativeSharedPtr<T> GetOrAlloc<T>(
            WorldAccessor world,
            BlobId blobId,
            Func<T> factory
        )
            where T : unmanaged => GetOrAlloc<T>(world.Heap, blobId, factory);

        /// <summary>
        /// Returns the existing native-shared blob at <paramref name="blobId"/> if cached,
        /// otherwise calls <paramref name="factory"/> to obtain a native allocation and
        /// takes ownership of it. The factory is only invoked on cache miss.
        /// See <see cref="AllocTakingOwnership{T}"/> for the ownership contract
        /// and <see cref="GetOrAlloc{T}(HeapAccessor, BlobId, Func{T})"/> for how to keep
        /// <paramref name="factory"/> allocation-free.
        /// </summary>
        public static NativeSharedPtr<T> GetOrAllocTakingOwnership<T>(
            HeapAccessor heap,
            BlobId blobId,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            if (heap.NativeSharedHeap.TryGetBlob<T>(blobId, out var ptr))
            {
                return ptr;
            }
            return heap.NativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, factory());
        }

        public static NativeSharedPtr<T> GetOrAllocTakingOwnership<T>(
            WorldAccessor world,
            BlobId blobId,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged => GetOrAllocTakingOwnership<T>(world.Heap, blobId, factory);

        public static NativeSharedPtr<T> AllocFrameScoped<T>(
            HeapAccessor heap,
            BlobId blobId,
            in T value
        )
            where T : unmanaged
        {
            heap.AssertCanAddInputsSystem();
            return heap.FrameScopedNativeSharedHeap.CreateBlob<T>(
                heap.FixedFrame,
                blobId,
                in value
            );
        }

        public static NativeSharedPtr<T> AllocFrameScoped<T>(HeapAccessor heap, BlobId blobId)
            where T : unmanaged
        {
            heap.AssertCanAddInputsSystem();
            return heap.FrameScopedNativeSharedHeap.CreateBlob<T>(heap.FixedFrame, blobId);
        }

        public static NativeSharedPtr<T> AllocFrameScoped<T>(
            WorldAccessor world,
            BlobId blobId,
            in T value
        )
            where T : unmanaged => AllocFrameScoped<T>(world.Heap, blobId, in value);

        public static NativeSharedPtr<T> AllocFrameScoped<T>(WorldAccessor world, BlobId blobId)
            where T : unmanaged => AllocFrameScoped<T>(world.Heap, blobId);

        public static bool TryGetFrameScoped<T>(
            HeapAccessor heap,
            BlobId blobId,
            out NativeSharedPtr<T> ptr
        )
            where T : unmanaged
        {
            heap.AssertCanAddInputsSystem();
            return heap.FrameScopedNativeSharedHeap.TryGetBlob<T>(heap.FixedFrame, blobId, out ptr);
        }

        public static bool TryGetFrameScoped<T>(
            WorldAccessor world,
            BlobId blobId,
            out NativeSharedPtr<T> ptr
        )
            where T : unmanaged => TryGetFrameScoped<T>(world.Heap, blobId, out ptr);
    }

    /// <summary>
    /// Reference-counted pointer to a shared native (unmanaged) heap allocation. Burst-compatible.
    /// Multiple entities can reference the same data via <see cref="BlobId"/>.
    /// <para>
    /// Allocate via <see cref="NativeSharedPtr.Alloc{T}(HeapAccessor, BlobId)"/>. Open a
    /// safety-checked view with <see cref="Read(HeapAccessor)"/> on the main thread, or
    /// <see cref="Read(in NativeSharedPtrResolver)"/> in Burst jobs. <see cref="Clone"/>
    /// increments the reference count; <see cref="Dispose(HeapAccessor)"/> decrements it and
    /// frees on zero.
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

        /// <summary>
        /// Opens a safety-checked read view. Main-thread only; jobs use
        /// <see cref="Read(in NativeSharedPtrResolver)"/>. Bridges the persistent heap's
        /// <c>_pendingAdds</c> so freshly-created blobs are readable before the next
        /// <c>FlushPendingOperations</c>.
        /// </summary>
        public readonly NativeSharedRead<T> Read(HeapAccessor heap)
        {
            return heap.NativeSharedHeap.Read(in this);
        }

        public readonly NativeSharedRead<T> Read(WorldAccessor world) => Read(world.Heap);

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="HeapAccessor.NativeSharedPtrResolver"/> or
        /// <see cref="NativeWorldAccessor.SharedPtrResolver"/>.
        /// </summary>
        public readonly NativeSharedRead<T> Read(in NativeSharedPtrResolver resolver)
        {
            var entry = resolver.ResolveEntry<T>(BlobId);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeSharedRead<T>(entry.Ptr.ToPointer(), entry.Safety);
#else
            return new NativeSharedRead<T>(entry.Ptr.ToPointer());
#endif
        }

        public readonly void Dispose(HeapAccessor heap)
        {
            TrecsAssert.That(
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
