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
                // A common cause: scheduling a Burst job that reads a NativeSharedPtr
                // created via CreateBlob in the same frame, before submission has
                // run FlushPendingOperations to move the entry into _allEntries.
                // See NativeSharedHeap's class docstring for the full invariant.
                throw new TrecsException(
                    $"NativeSharedPtrResolver could not resolve blob {address.Value} for type {typeof(T)}. "
                        + "Blob was either never created, already disposed, or created this frame and not yet flushed."
                );
            }

            if (entry.TypeHash != TypeHash<T>.Value)
            {
                throw new TrecsException(
                    $"Type hash mismatch for blob {address.Value}: stored {entry.TypeHash} != requested {TypeHash<T>.Value} ({typeof(T)})"
                );
            }

            return entry.Ptr.ToPointer();
        }
    }
}
