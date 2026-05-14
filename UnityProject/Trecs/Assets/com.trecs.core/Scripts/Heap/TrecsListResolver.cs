using System;
using System.ComponentModel;
using Trecs.Internal;

namespace Trecs.Internal
{
    /// <summary>
    /// In-memory header for a single <see cref="TrecsList{T}"/> allocation. Lives at a stable
    /// address on the heap for the lifetime of the list; the data pointer changes on grow but
    /// the header pointer does not. Wrapper structs cache the header pointer at Open time.
    ///
    /// <para><c>DataHandle</c> owns the variable-sized data slot in the same
    /// <see cref="NativeChunkStore"/> as the header. <c>Data</c> is a fast-access cache of
    /// that slot's address, refreshed on every grow and on deserialize. Jobs read
    /// <c>Data</c> directly without touching the chunk-store resolver, so element access
    /// stays a single indirection.</para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct TrecsListHeader
    {
        public IntPtr Data;
        public PtrHandle DataHandle;
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
    /// <see cref="NativeWorldAccessor.TrecsListResolver"/>. Pass to
    /// <see cref="TrecsList{T}.Read(in NativeTrecsListResolver)"/> or
    /// <see cref="TrecsList{T}.Write(in NativeTrecsListResolver)"/>.
    /// </summary>
    public readonly unsafe struct NativeTrecsListResolver
    {
        readonly NativeChunkStoreResolver _chunkResolver;

        public NativeTrecsListResolver(NativeChunkStoreResolver chunkResolver)
        {
            _chunkResolver = chunkResolver;
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
