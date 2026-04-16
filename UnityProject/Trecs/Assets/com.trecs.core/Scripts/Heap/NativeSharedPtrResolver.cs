using System;
using System.ComponentModel;
using Trecs.Internal;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NativeSharedHeapEntry
    {
        public readonly int TypeHash;
        public readonly IntPtr Ptr;

        public NativeSharedHeapEntry(int typeHash, IntPtr ptr)
        {
            TypeHash = typeHash;
            Ptr = ptr;
        }
    }
}

namespace Trecs
{
    /// <summary>
    /// Burst-compatible resolver that maps <see cref="BlobId"/> values to native memory addresses
    /// for <see cref="NativeSharedPtr{T}"/> lookups inside jobs. Obtain via
    /// <see cref="HeapAccessor.NativeSharedPtrResolver"/> or <see cref="NativeWorldAccessor.SharedPtrResolver"/>.
    /// </summary>
    public readonly unsafe struct NativeSharedPtrResolver
    {
        readonly NativeDenseDictionary<BlobId, NativeSharedHeapEntry> _entries;

        public NativeSharedPtrResolver(NativeDenseDictionary<BlobId, NativeSharedHeapEntry> entries)
        {
            _entries = entries;
        }

        public unsafe void* ResolveUnsafePtr<T>(BlobId address)
            where T : unmanaged
        {
            Assert.That(!address.IsNull, "Attempted to resolve null blob address");

            if (!_entries.TryGetValue(address, out var entry))
            {
                throw new TrecsException(
                    $"Attempted to resolve invalid heap memory address ({address.Value}) for type {typeof(T)}"
                );
            }

            if (entry.TypeHash != TypeHash<T>.Value)
            {
                throw new TrecsException(
                    $"Type hash mismatch: {entry.TypeHash} != {TypeHash<T>.Value}"
                );
            }

            return entry.Ptr.ToPointer();
        }
    }
}
