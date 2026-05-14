using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Burst-compatible resolver that maps <see cref="PtrHandle"/> values to native memory for
    /// <see cref="NativeUniquePtr{T}"/> lookups inside jobs. Wraps the underlying
    /// <see cref="NativeChunkStoreResolver"/> with type-hash checking. Persistent and
    /// frame-scoped allocations share the same chunk store, so there is only one resolver path
    /// regardless of which heap the handle came from. Obtain via
    /// <see cref="HeapAccessor.NativeUniquePtrResolver"/> or
    /// <see cref="NativeWorldAccessor.UniquePtrResolver"/>. Pass to
    /// <see cref="NativeUniquePtr{T}.Read(in NativeUniquePtrResolver)"/> or
    /// <see cref="NativeUniquePtr{T}.Write(in NativeUniquePtrResolver)"/>.
    /// </summary>
    public readonly unsafe struct NativeUniquePtrResolver
    {
        readonly NativeChunkStoreResolver _chunkResolver;

        public NativeUniquePtrResolver(NativeChunkStoreResolver chunkResolver)
        {
            _chunkResolver = chunkResolver;
        }

        internal NativeChunkStoreEntry ResolveEntry<T>(uint address)
            where T : unmanaged
        {
            var entry = _chunkResolver.ResolveEntry(new PtrHandle(address));
            AssertTypeHashMatches<T>(entry.TypeHash);
            return entry;
        }

        internal unsafe void* ResolveUnsafePtr<T>(uint address)
            where T : unmanaged
        {
            return ResolveEntry<T>(address).Address.ToPointer();
        }

        static void AssertTypeHashMatches<T>(int storedHash)
            where T : unmanaged
        {
            if (storedHash != TypeHash<T>.Value)
            {
                throw new TrecsException(
                    $"Type hash mismatch: {storedHash} != {TypeHash<T>.Value}"
                );
            }
        }
    }
}
