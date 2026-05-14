using System;
using System.Runtime.InteropServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="TrecsList{T}"/>. Allocation goes through here;
    /// per-instance operations (<c>Read</c>, <c>Write</c>, <c>EnsureCapacity</c>,
    /// <c>Dispose</c>) live on the struct itself.
    /// </summary>
    public static class TrecsList
    {
        public static TrecsList<T> Alloc<T>(HeapAccessor heap, int initialCapacity = 0)
            where T : unmanaged
        {
            heap.AssertCanAllocatePersistent();
            return heap.TrecsListHeap.Alloc<T>(initialCapacity);
        }

        public static TrecsList<T> Alloc<T>(WorldAccessor world, int initialCapacity = 0)
            where T : unmanaged => Alloc<T>(world.Heap, initialCapacity);
    }

    /// <summary>
    /// A growable list of unmanaged values whose storage is owned by the
    /// world's <see cref="TrecsListHeap"/>. Designed to live as a 4-byte
    /// value on ECS components, so a component can carry a dynamically-
    /// sized collection without going through <c>NativeUniquePtr</c>.
    ///
    /// <para>The struct itself only carries a <see cref="PtrHandle"/>;
    /// allocating, growing, reading, and writing all go through
    /// <see cref="HeapAccessor"/> / <see cref="NativeWorldAccessor"/>:</para>
    ///
    /// <list type="bullet">
    /// <item><description><see cref="HeapAccessor.AllocTrecsList{T}(int)"/> /
    ///   <see cref="HeapAccessor.AllocTrecsList{T}()"/> creates the list with an
    ///   initial capacity.</description></item>
    /// <item><description><see cref="HeapAccessor.Read{T}(in TrecsList{T})"/> /
    ///   <see cref="NativeTrecsListResolver.Read{T}"/> opens a read view; many readers
    ///   in parallel are allowed because the wrapper is
    ///   <c>[NativeContainerIsReadOnly]</c>.</description></item>
    /// <item><description><see cref="HeapAccessor.Write{T}(in TrecsList{T})"/> /
    ///   <see cref="NativeTrecsListResolver.Write{T}"/> opens a write view that
    ///   supports <c>Add</c>, <c>RemoveAt</c>, <c>Clear</c>, indexer, etc. The
    ///   per-allocation <c>AtomicSafetyHandle</c> on the wrapper lets Unity's job
    ///   walker detect cross-job conflicts at schedule time, just like on
    ///   <see cref="NativeUniquePtr{T}"/>.</description></item>
    /// <item><description><see cref="Dispose(HeapAccessor)"/> frees the backing
    ///   storage. The struct is exclusive-ownership: copying it produces two
    ///   handles that both refer to the same backing storage, and the per-handle
    ///   safety identity catches concurrent writers on those copies.</description></item>
    /// </list>
    ///
    /// <para><b>Growth.</b> <see cref="TrecsListWrite{T}.Add"/> reallocates when
    /// <c>Count == Capacity</c>. The header struct that holds the backing pointer
    /// lives at a stable address on the heap, so wrappers cached across grows stay
    /// valid — they re-read the (data, count, capacity) tuple from the header
    /// on each access.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TrecsList<T> : IEquatable<TrecsList<T>>
        where T : unmanaged
    {
        public readonly PtrHandle Handle;

        public TrecsList(PtrHandle handle)
        {
            Handle = handle;
        }

        public readonly bool IsNull => Handle.IsNull;

        public readonly void Dispose(HeapAccessor heap)
        {
            heap.TrecsListHeap.DisposeEntry(Handle.Value);
        }

        public readonly void Dispose(WorldAccessor world) => Dispose(world.Heap);

        /// <summary>
        /// Grows this list's backing data buffer so it can hold at least
        /// <paramref name="minCapacity"/> elements without further reallocation. Doubles
        /// geometrically past the existing capacity. Main-thread only — call this before
        /// scheduling any job that appends to the list, since
        /// <see cref="TrecsListWrite{T}.Add"/> cannot grow.
        /// </summary>
        public readonly void EnsureCapacity(HeapAccessor heap, int minCapacity)
        {
            heap.TrecsListHeap.EnsureCapacity(in this, minCapacity);
        }

        public readonly void EnsureCapacity(WorldAccessor world, int minCapacity) =>
            EnsureCapacity(world.Heap, minCapacity);

        /// <summary>
        /// Opens a safety-checked read view. Main-thread only; for in-job access use the
        /// <see cref="NativeTrecsListResolver"/> overload.
        /// </summary>
        public readonly TrecsListRead<T> Read(HeapAccessor heap) =>
            heap.TrecsListHeap.Read(in this);

        public readonly TrecsListRead<T> Read(WorldAccessor world) => Read(world.Heap);

        /// <summary>
        /// Opens a safety-checked write view. Main-thread only; for in-job access use the
        /// <see cref="NativeTrecsListResolver"/> overload.
        /// </summary>
        public readonly TrecsListWrite<T> Write(HeapAccessor heap) =>
            heap.TrecsListHeap.Write(in this);

        public readonly TrecsListWrite<T> Write(WorldAccessor world) => Write(world.Heap);

        /// <summary>
        /// Burst-compatible read view. Pass a resolver obtained via
        /// <see cref="HeapAccessor.NativeTrecsListResolver"/> or
        /// <see cref="NativeWorldAccessor.TrecsListResolver"/>.
        /// </summary>
        public readonly unsafe TrecsListRead<T> Read(in NativeTrecsListResolver resolver)
        {
            var entry = resolver.ResolveEntry<T>(Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListRead<T>((TrecsListHeader*)entry.Address.ToPointer(), entry.Safety);
#else
            return new TrecsListRead<T>((TrecsListHeader*)entry.Address.ToPointer());
#endif
        }

        /// <summary>
        /// Burst-compatible write view. Pass a resolver obtained via
        /// <see cref="HeapAccessor.NativeTrecsListResolver"/> or
        /// <see cref="NativeWorldAccessor.TrecsListResolver"/>.
        /// </summary>
        public readonly unsafe TrecsListWrite<T> Write(in NativeTrecsListResolver resolver)
        {
            var entry = resolver.ResolveEntry<T>(Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListWrite<T>((TrecsListHeader*)entry.Address.ToPointer(), entry.Safety);
#else
            return new TrecsListWrite<T>((TrecsListHeader*)entry.Address.ToPointer());
#endif
        }

        public readonly bool Equals(TrecsList<T> other) => Handle.Equals(other.Handle);

        public override readonly bool Equals(object obj) =>
            obj is TrecsList<T> other && Equals(other);

        public override readonly int GetHashCode() => Handle.GetHashCode();

        public static bool operator ==(TrecsList<T> left, TrecsList<T> right) => left.Equals(right);

        public static bool operator !=(TrecsList<T> left, TrecsList<T> right) =>
            !left.Equals(right);
    }
}
