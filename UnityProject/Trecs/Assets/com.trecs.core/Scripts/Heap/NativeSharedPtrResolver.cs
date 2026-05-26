using System;
using System.ComponentModel;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NativeSharedHeapEntry
    {
        public readonly int TypeHash;
        public readonly IntPtr Ptr;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public readonly AtomicSafetyHandle Safety;

        public NativeSharedHeapEntry(int typeHash, IntPtr ptr, AtomicSafetyHandle safety)
        {
            TypeHash = typeHash;
            Ptr = ptr;
            Safety = safety;
        }
#else
        public NativeSharedHeapEntry(int typeHash, IntPtr ptr)
        {
            TypeHash = typeHash;
            Ptr = ptr;
        }
#endif
    }

    /// <summary>
    /// Burst-compatible resolver that maps <see cref="BlobId"/> values to native memory
    /// for <see cref="NativeSharedPtr{T}"/> lookups inside jobs. Obtain via
    /// <see cref="WorldAccessor.NativeSharedPtrResolver"/> or
    /// <see cref="NativeWorldAccessor.SharedPtrResolver"/>. Open a typed view via
    /// <see cref="NativeSharedPtr{T}.Read(in NativeSharedPtrResolver)"/>.
    /// </summary>
    public readonly unsafe struct NativeSharedPtrResolver
    {
        [Unity.Collections.ReadOnly]
        readonly NativeHashMap<BlobId, NativeSharedHeapEntry> _entries;

        public NativeSharedPtrResolver(NativeHashMap<BlobId, NativeSharedHeapEntry> entries)
        {
            _entries = entries;
        }

        internal NativeSharedHeapEntry ResolveEntry<T>(BlobId address)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!address.IsNull, "Attempted to resolve null blob address");

            // A common cause of the resolve failure below: scheduling a Burst job
            // that reads a NativeSharedPtr created via CreateBlob in the same
            // frame, before submission has run FlushPendingOperations to move the
            // entry into _allEntries. See NativeSharedHeap's class docstring for
            // the full invariant.
            TrecsAssert.That(
                _entries.TryGetValue(address, out var entry),
                "NativeSharedPtrResolver could not resolve blob {0} for type hash {1}. "
                    + "Blob was either never created, already disposed, or created this frame and not yet flushed.",
                address.Value,
                TypeId<T>.Value.Value
            );

            TrecsAssert.That(
                entry.TypeHash == TypeId<T>.Value.Value,
                "Type hash mismatch for blob {0}: stored {1} != requested {2}",
                address.Value,
                entry.TypeHash,
                TypeId<T>.Value.Value
            );

            return entry;
        }
    }
}
