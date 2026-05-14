using System;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="NativeUniquePtr{T}"/>. Per-instance operations
    /// (<c>Read</c>, <c>Write</c>, <c>Dispose</c>) live on the struct itself.
    /// </summary>
    public static class NativeUniquePtr
    {
        public static NativeUniquePtr<T> Alloc<T>(HeapAccessor heap, in T value)
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeUniqueHeap.Alloc<T>(in value);
        }

        public static NativeUniquePtr<T> Alloc<T>(HeapAccessor heap)
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeUniqueHeap.Alloc<T>();
        }

        public static NativeUniquePtr<T> Alloc<T>(WorldAccessor world, in T value)
            where T : unmanaged => Alloc<T>(world.Heap, in value);

        public static NativeUniquePtr<T> Alloc<T>(WorldAccessor world)
            where T : unmanaged => Alloc<T>(world.Heap);

        /// <summary>
        /// Takes ownership of an existing native allocation without copying.
        /// See <see cref="NativeUniquePtr{T}"/> docs for the ownership contract.
        /// </summary>
        public static NativeUniquePtr<T> AllocTakingOwnership<T>(
            HeapAccessor heap,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.NativeUniqueHeap.AllocTakingOwnership<T>(alloc);
        }

        public static NativeUniquePtr<T> AllocTakingOwnership<T>(
            WorldAccessor world,
            NativeBlobAllocation alloc
        )
            where T : unmanaged => AllocTakingOwnership<T>(world.Heap, alloc);

        public static NativeUniquePtr<T> AllocFrameScoped<T>(HeapAccessor heap, in T value)
            where T : unmanaged
        {
            heap.AssertCanAddInputsSystem();
            return heap.FrameScopedNativeUniqueHeap.Alloc<T>(heap.FixedFrame, in value);
        }

        public static NativeUniquePtr<T> AllocFrameScoped<T>(HeapAccessor heap)
            where T : unmanaged
        {
            heap.AssertCanAddInputsSystem();
            return heap.FrameScopedNativeUniqueHeap.Alloc<T>(heap.FixedFrame);
        }

        public static NativeUniquePtr<T> AllocFrameScoped<T>(WorldAccessor world, in T value)
            where T : unmanaged => AllocFrameScoped<T>(world.Heap, in value);

        public static NativeUniquePtr<T> AllocFrameScoped<T>(WorldAccessor world)
            where T : unmanaged => AllocFrameScoped<T>(world.Heap);

        public static NativeUniquePtr<T> AllocFrameScopedTakingOwnership<T>(
            HeapAccessor heap,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            heap.AssertCanAddInputsSystem();
            return heap.FrameScopedNativeUniqueHeap.AllocTakingOwnership<T>(heap.FixedFrame, alloc);
        }

        public static NativeUniquePtr<T> AllocFrameScopedTakingOwnership<T>(
            WorldAccessor world,
            NativeBlobAllocation alloc
        )
            where T : unmanaged => AllocFrameScopedTakingOwnership<T>(world.Heap, alloc);
    }

    /// <summary>
    /// Exclusive-ownership pointer to a native (unmanaged) heap allocation. Burst-compatible.
    /// <para>
    /// Allocate via <see cref="HeapAccessor.AllocNativeUnique{T}"/>. Open a safety-checked view
    /// with <see cref="HeapAccessor.Read{T}"/> / <see cref="HeapAccessor.Write{T}"/> on the main
    /// thread, or <see cref="NativeUniquePtrResolver.Read{T}"/> /
    /// <see cref="NativeUniquePtrResolver.Write{T}"/> in Burst jobs. Frame-scoped variants are
    /// cleaned up automatically; persistent pointers must be disposed explicitly via
    /// <see cref="Dispose(HeapAccessor)"/>.
    /// </para>
    /// <para>
    /// The persistent struct stores only a <see cref="PtrHandle"/> — it is intentionally cheap
    /// to copy and store on components. Per-allocation <c>AtomicSafetyHandle</c>s live on the
    /// owning heap and are attached to the <see cref="NativeUniqueRead{T}"/> /
    /// <see cref="NativeUniqueWrite{T}"/> wrappers at Open time, so Unity's job-safety walker
    /// can detect cross-job read/write conflicts at schedule time. There is intentionally no
    /// <c>Clone</c> — exclusive ownership means duplicating the ptr would create two owners of
    /// the same heap entry.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly unsafe struct NativeUniquePtr<T> : IEquatable<NativeUniquePtr<T>>
        where T : unmanaged
    {
        public readonly PtrHandle Handle;

        public NativeUniquePtr(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly void Dispose(HeapAccessor heap)
        {
            TrecsAssert.That(
                !heap.FrameScopedNativeUniqueHeap.ContainsEntry(Handle.Value),
                "Frame-scoped NativeUniquePtr must not be manually disposed"
            );
            heap.NativeUniqueHeap.DisposeEntry(Handle.Value);
        }

        public readonly void Dispose(WorldAccessor world) => Dispose(world.Heap);

        /// <summary>
        /// Opens a safety-checked read view. Main-thread only; for in-job access use the
        /// <see cref="NativeUniquePtrResolver"/> overload. Checks both persistent and
        /// frame-scoped storage transparently.
        /// </summary>
        public readonly NativeUniqueRead<T> Read(HeapAccessor heap)
        {
            if (heap.FrameScopedNativeUniqueHeap.ContainsEntry(Handle.Value))
            {
                var entry = heap.FrameScopedNativeUniqueHeap.ResolveEntry<T>(
                    Handle.Value,
                    heap.FixedFrame
                );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                return new NativeUniqueRead<T>(entry.Address.ToPointer(), entry.Safety);
#else
                return new NativeUniqueRead<T>(entry.Address.ToPointer());
#endif
            }
            return heap.NativeUniqueHeap.Read(in this);
        }

        public readonly NativeUniqueRead<T> Read(WorldAccessor world) => Read(world.Heap);

        /// <summary>
        /// Opens a safety-checked write view. Main-thread only; for in-job access use the
        /// <see cref="NativeUniquePtrResolver"/> overload. Checks both persistent and
        /// frame-scoped storage transparently.
        /// </summary>
        public readonly NativeUniqueWrite<T> Write(HeapAccessor heap)
        {
            if (heap.FrameScopedNativeUniqueHeap.ContainsEntry(Handle.Value))
            {
                var entry = heap.FrameScopedNativeUniqueHeap.ResolveEntry<T>(
                    Handle.Value,
                    heap.FixedFrame
                );
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                return new NativeUniqueWrite<T>(entry.Address.ToPointer(), entry.Safety);
#else
                return new NativeUniqueWrite<T>(entry.Address.ToPointer());
#endif
            }
            return heap.NativeUniqueHeap.Write(in this);
        }

        public readonly NativeUniqueWrite<T> Write(WorldAccessor world) => Write(world.Heap);

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="HeapAccessor.NativeUniquePtrResolver"/> or
        /// <see cref="NativeWorldAccessor.UniquePtrResolver"/>. Persistent and frame-scoped
        /// allocations share the same resolver path.
        /// </summary>
        public readonly NativeUniqueRead<T> Read(in NativeUniquePtrResolver resolver)
        {
            var entry = resolver.ResolveEntry<T>(Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueRead<T>(entry.Address.ToPointer(), entry.Safety);
#else
            return new NativeUniqueRead<T>(entry.Address.ToPointer());
#endif
        }

        /// <summary>
        /// Burst-compatible write view. See <see cref="Read(in NativeUniquePtrResolver)"/>.
        /// </summary>
        public readonly NativeUniqueWrite<T> Write(in NativeUniquePtrResolver resolver)
        {
            var entry = resolver.ResolveEntry<T>(Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new NativeUniqueWrite<T>(entry.Address.ToPointer(), entry.Safety);
#else
            return new NativeUniqueWrite<T>(entry.Address.ToPointer());
#endif
        }

        public readonly bool IsNull
        {
            get { return Handle.IsNull; }
        }

        /// <remarks>
        /// Equality compares only <see cref="Handle"/>. <see cref="NativeUniquePtr{T}"/> has no
        /// separate blob ID — the handle <i>is</i> the identity (each handle uniquely owns
        /// one heap entry). The shared variants (<see cref="SharedPtr{T}"/> /
        /// <see cref="NativeSharedPtr{T}"/>) additionally compare a <see cref="BlobId"/>
        /// because multiple handles can reference the same underlying blob; that doesn't
        /// apply here.
        /// </remarks>
        public readonly bool Equals(NativeUniquePtr<T> other)
        {
            return Handle.Equals(other.Handle);
        }

        public override readonly bool Equals(object obj)
        {
            return obj is NativeUniquePtr<T> other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return Handle.GetHashCode();
        }

        public static bool operator ==(NativeUniquePtr<T> left, NativeUniquePtr<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(NativeUniquePtr<T> left, NativeUniquePtr<T> right)
        {
            return !left.Equals(right);
        }
    }
}
