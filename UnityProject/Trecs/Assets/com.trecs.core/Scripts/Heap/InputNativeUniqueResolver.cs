using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Burst-compatible resolver for <see cref="InputNativeUniquePtr{T}"/> reads
    /// inside jobs. Wraps the <see cref="InputNativeUniqueHeap"/>'s allocation
    /// table; copy by value into job structs.
    ///
    /// <para>Resolution is a single hash-table lookup. The backing
    /// <see cref="NativeHashMap{InputPtrHandle,InputAllocation}"/> is
    /// owned by the heap and remains valid for the heap's lifetime — entries
    /// come and go as input frames are allocated and trimmed, but the storage
    /// handle held by this resolver does not need to be refreshed.</para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct InputNativeUniqueResolver
    {
        readonly NativeHashMap<InputPtrHandle, InputAllocation> _allocations;

        internal InputNativeUniqueResolver(
            NativeHashMap<InputPtrHandle, InputAllocation> allocations
        )
        {
            _allocations = allocations;
        }

        /// <summary>
        /// Burst-friendly type-checked resolve. The per-allocation
        /// <see cref="InputAllocation.TypeHash"/> tag is verified against
        /// <see cref="TypeId{T}.Value"/> so a wrong-<typeparamref name="T"/>
        /// read fires in release builds too (the assert uses
        /// <see cref="TrecsAssert"/>, not the debug-only variant).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void* Resolve<T>(InputPtrHandle handle)
            where T : unmanaged
        {
            TrecsAssert.That(!handle.IsNull, "Cannot resolve null InputPtrHandle");
            var found = _allocations.TryGetValue(handle, out var alloc);
            TrecsAssert.That(
                found,
                "InputNativeUniqueResolver: handle {0} not found (already-trimmed frame, stale handle, or wrong heap?)",
                handle.Value
            );
            TrecsAssert.That(
                alloc.TypeHash == TypeId<T>.Value,
                "InputNativeUniqueResolver: handle {0} type mismatch (allocated as TypeId={1}, read with TypeId={2})",
                handle.Value,
                alloc.TypeHash.Value,
                TypeId<T>.Value.Value
            );
            return (void*)alloc.Ptr;
        }
    }

    /// <summary>
    /// Per-allocation record stored in <see cref="InputNativeUniqueHeap"/>'s
    /// allocation table. <see cref="Ptr"/> is owned by the heap and freed when
    /// the allocating frame is trimmed; <see cref="Size"/> is the byte length
    /// of the allocation, used by the Serialize path; <see cref="TypeHash"/>
    /// is the <see cref="TypeId"/> the allocation was created with, used by
    /// <see cref="InputNativeUniqueResolver.Resolve{T}"/> and the main-thread
    /// <c>InputNativeUniqueHeap.ResolveUnsafePtr&lt;T&gt;</c> to catch wrong-T
    /// reads structurally (the tag round-trips through Serialize/Deserialize).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct InputAllocation
    {
        public readonly IntPtr Ptr;
        public readonly int Size;
        public readonly TypeId TypeHash;

        public InputAllocation(IntPtr ptr, int size, TypeId typeHash)
        {
            Ptr = ptr;
            Size = size;
            TypeHash = typeHash;
        }
    }
}
