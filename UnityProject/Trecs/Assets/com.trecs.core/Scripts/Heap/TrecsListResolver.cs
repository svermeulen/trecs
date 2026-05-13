using System;
using System.ComponentModel;
using Trecs.Internal;

namespace Trecs.Internal
{
    /// <summary>
    /// In-memory header for a single <see cref="TrecsList{T}"/> allocation. Lives at a stable
    /// address on the heap for the lifetime of the list; the data pointer changes on grow but
    /// the header pointer does not. Wrapper structs cache the header pointer at Open time.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct TrecsListHeader
    {
        public IntPtr Data;
        public int Count;
        public int Capacity;
        public int ElementSize;
        public int ElementAlign;
    }
}

namespace Trecs
{
    /// <summary>
    /// Burst-compatible resolver that maps <see cref="PtrHandle"/> values to
    /// <see cref="TrecsListHeader"/> pointers. Backed by the shared
    /// <see cref="NativeChunkStoreResolver"/> with an extra type-hash check on the way out.
    /// Obtain via <see cref="HeapAccessor.NativeTrecsListResolver"/> or
    /// <see cref="NativeWorldAccessor.TrecsListResolver"/>. Open a typed view with
    /// <see cref="Read{T}"/> or <see cref="Write{T}"/>.
    /// </summary>
    public readonly unsafe struct NativeTrecsListResolver
    {
        readonly NativeChunkStoreResolver _chunkResolver;

        public NativeTrecsListResolver(NativeChunkStoreResolver chunkResolver)
        {
            _chunkResolver = chunkResolver;
        }

        public TrecsListRead<T> Read<T>(in TrecsList<T> list)
            where T : unmanaged
        {
            var entry = ResolveEntry<T>(list.Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListRead<T>((TrecsListHeader*)entry.Address.ToPointer(), entry.Safety);
#else
            return new TrecsListRead<T>((TrecsListHeader*)entry.Address.ToPointer());
#endif
        }

        public TrecsListWrite<T> Write<T>(in TrecsList<T> list)
            where T : unmanaged
        {
            var entry = ResolveEntry<T>(list.Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListWrite<T>((TrecsListHeader*)entry.Address.ToPointer(), entry.Safety);
#else
            return new TrecsListWrite<T>((TrecsListHeader*)entry.Address.ToPointer());
#endif
        }

        internal NativeChunkStoreEntry ResolveEntry<T>(uint address)
            where T : unmanaged
        {
            var entry = _chunkResolver.ResolveEntry(new PtrHandle(address));
            if (entry.TypeHash != TypeHash<T>.Value)
            {
                throw new TrecsException(
                    $"Type hash mismatch for TrecsList handle {address}: "
                        + $"stored {entry.TypeHash} != requested {TypeHash<T>.Value} ({typeof(T)})"
                );
            }
            return entry;
        }
    }
}
