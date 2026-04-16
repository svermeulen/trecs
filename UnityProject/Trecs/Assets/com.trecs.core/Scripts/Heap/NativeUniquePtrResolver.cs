using System;
using System.ComponentModel;
using Trecs.Internal;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NativeUniqueHeapEntry
    {
        public readonly int TypeHash;
        public readonly IntPtr Ptr;

        public NativeUniqueHeapEntry(int typeHash, IntPtr ptr)
        {
            TypeHash = typeHash;
            Ptr = ptr;
        }
    }
}

namespace Trecs
{
    /// <summary>
    /// Burst-compatible resolver that maps <see cref="PtrHandle"/> values to native memory addresses
    /// for <see cref="NativeUniquePtr{T}"/> lookups inside jobs. Checks both persistent and
    /// frame-scoped heaps. Obtain via <see cref="HeapAccessor.NativeUniquePtrResolver"/> or
    /// <see cref="NativeWorldAccessor.UniquePtrResolver"/>.
    /// </summary>
    public readonly unsafe struct NativeUniquePtrResolver
    {
        readonly NativeDenseDictionary<uint, NativeUniqueHeapEntry> _persistentEntries;
        readonly NativeDenseDictionary<uint, NativeUniqueHeapEntry> _frameScopedEntries;

        public NativeUniquePtrResolver(
            NativeDenseDictionary<uint, NativeUniqueHeapEntry> persistentEntries,
            NativeDenseDictionary<uint, NativeUniqueHeapEntry> frameScopedEntries
        )
        {
            _persistentEntries = persistentEntries;
            _frameScopedEntries = frameScopedEntries;
        }

        public unsafe void* ResolveUnsafePtr<T>(uint address)
            where T : unmanaged
        {
            Assert.That(address != 0, "Attempted to resolve null address");

            if (_persistentEntries.TryGetValue(address, out var entry))
            {
                if (entry.TypeHash != TypeHash<T>.Value)
                {
                    throw new TrecsException(
                        $"Type hash mismatch: {entry.TypeHash} != {TypeHash<T>.Value}"
                    );
                }

                return entry.Ptr.ToPointer();
            }

            if (_frameScopedEntries.TryGetValue(address, out entry))
            {
                if (entry.TypeHash != TypeHash<T>.Value)
                {
                    throw new TrecsException(
                        $"Type hash mismatch: {entry.TypeHash} != {TypeHash<T>.Value}"
                    );
                }

                return entry.Ptr.ToPointer();
            }

            throw new TrecsException(
                $"Attempted to resolve invalid heap memory address ({address}) for type {typeof(T)}"
            );
        }
    }
}
