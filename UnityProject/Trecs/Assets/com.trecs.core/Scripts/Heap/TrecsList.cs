using System;
using System.Runtime.InteropServices;

namespace Trecs
{
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

        public readonly bool Equals(TrecsList<T> other) => Handle.Equals(other.Handle);

        public override readonly bool Equals(object obj) =>
            obj is TrecsList<T> other && Equals(other);

        public override readonly int GetHashCode() => Handle.GetHashCode();

        public static bool operator ==(TrecsList<T> left, TrecsList<T> right) => left.Equals(right);

        public static bool operator !=(TrecsList<T> left, TrecsList<T> right) =>
            !left.Equals(right);
    }
}
