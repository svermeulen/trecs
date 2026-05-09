using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Exclusive-ownership pointer to a native (unmanaged) heap allocation. Burst-compatible.
    /// Resolve to a <c>ref readonly T</c> via <see cref="Get(in NativeUniquePtrResolver)"/> in jobs
    /// or <see cref="Get(HeapAccessor)"/> on the main thread. For mutable access use
    /// <see cref="NativeUniquePtrExtensions.GetMut{T}"/>.
    /// <para>
    /// Allocate via <see cref="HeapAccessor.AllocNativeUnique{T}"/>. Frame-scoped variants
    /// are cleaned up automatically; persistent pointers must be disposed explicitly.
    /// </para>
    /// <para>
    /// Public verb set: <c>GetUnsafePtr</c>, <c>Get</c>, <c>Dispose</c>, <c>IsNull</c>, plus
    /// the <c>GetMut</c> / <c>Set</c> extensions in <see cref="NativeUniquePtrExtensions"/>.
    /// <c>Get</c> / <c>GetUnsafePtr</c> have job-side overloads
    /// (<see cref="NativeUniquePtrResolver"/> / <see cref="NativeWorldAccessor"/>) and main-thread
    /// overloads (<see cref="HeapAccessor"/> / <see cref="WorldAccessor"/>). There is intentionally
    /// no <c>Clone</c> — exclusive ownership means duplicating the ptr would create two owners of
    /// the same heap entry. <c>Dispose</c> is main-thread-only by design (mutates heap bookkeeping
    /// not exposed to Burst jobs). There is no public <c>TryGet</c> / <c>CanGet</c>: a live
    /// <see cref="NativeUniquePtr{T}"/> is expected to resolve, mirroring
    /// <see cref="NativeSharedPtr{T}"/>.
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe void* GetUnsafePtr(in NativeUniquePtrResolver resolver)
        {
            return resolver.ResolveUnsafePtr<T>(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe void* GetUnsafePtr(HeapAccessor heap)
        {
            return heap.NativeUniqueHeap.ResolveUnsafePtr<T>(Handle.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe void* GetUnsafePtr(WorldAccessor accessor)
        {
            return GetUnsafePtr(accessor.Heap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(in NativeUniquePtrResolver resolver)
        {
            return ref UnsafeUtility.AsRef<T>(GetUnsafePtr(resolver));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(HeapAccessor heap)
        {
            return ref UnsafeUtility.AsRef<T>(GetUnsafePtr(heap));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(WorldAccessor accessor)
        {
            return ref Get(accessor.Heap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly unsafe ref readonly T Get(in NativeWorldAccessor accessor)
        {
            return ref UnsafeUtility.AsRef<T>(
                accessor.UniquePtrResolver.ResolveUnsafePtr<T>(Handle.Value)
            );
        }

        public readonly void Dispose(HeapAccessor heap)
        {
            Assert.That(
                !heap.FrameScopedNativeUniqueHeap.ContainsEntry(Handle.Value),
                "Frame-scoped NativeUniquePtr must not be manually disposed"
            );
            heap.NativeUniqueHeap.DisposeEntry(Handle.Value);
        }

        public readonly void Dispose(WorldAccessor world) => Dispose(world.Heap);

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
