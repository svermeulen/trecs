using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct InputNativeSharedHeapEntry
    {
        public readonly int TypeHash;
        public readonly IntPtr Ptr;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public readonly AtomicSafetyHandle Safety;

        public InputNativeSharedHeapEntry(int typeHash, IntPtr ptr, AtomicSafetyHandle safety)
        {
            TypeHash = typeHash;
            Ptr = ptr;
            Safety = safety;
        }
#else
        public InputNativeSharedHeapEntry(int typeHash, IntPtr ptr)
        {
            TypeHash = typeHash;
            Ptr = ptr;
        }
#endif
    }

    /// <summary>
    /// Burst-compatible resolver for <see cref="InputNativeSharedPtr{T}"/> lookups inside jobs.
    /// Wraps a <see cref="NativeHashMap{BlobId,InputNativeSharedHeapEntry}"/> populated during
    /// the input phase. Safe without a pending queue because input allocations and job reads
    /// occur in non-overlapping phases.
    /// </summary>
    public readonly struct InputNativeSharedPtrResolver
    {
        [Unity.Collections.ReadOnly]
        readonly NativeHashMap<BlobId, InputNativeSharedHeapEntry> _entries;

        public InputNativeSharedPtrResolver(
            NativeHashMap<BlobId, InputNativeSharedHeapEntry> entries
        )
        {
            _entries = entries;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InputNativeSharedHeapEntry ResolveEntry<T>(BlobId blobId)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!blobId.IsNull, "Attempted to resolve null blob address");

            TrecsAssert.That(
                _entries.TryGetValue(blobId, out var entry),
                "InputNativeSharedPtrResolver could not resolve blob {0} for type hash {1}.",
                blobId.Value,
                TypeId<T>.Value.Value
            );

            TrecsAssert.That(
                entry.TypeHash == TypeId<T>.Value.Value,
                "Type hash mismatch for blob {0}: stored {1} != requested {2}",
                blobId.Value,
                entry.TypeHash,
                TypeId<T>.Value.Value
            );

            return entry;
        }
    }
}
