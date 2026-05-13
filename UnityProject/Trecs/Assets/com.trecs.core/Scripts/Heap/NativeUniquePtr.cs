using System;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
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
