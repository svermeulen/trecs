using System;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Manages the storage backing <see cref="TrecsList{T}"/>. Both the stable
    /// <see cref="TrecsListHeader"/> and the variable-sized data buffer it points at live
    /// in the shared <see cref="NativeChunkStore"/>. Grow operations are main-thread-only —
    /// jobs cannot grow lists because the chunk store's allocator path isn't Burst-callable.
    /// Pre-size with <see cref="TrecsList{T}.EnsureCapacity(HeapAccessor, int)"/> before
    /// scheduling jobs that <c>Add</c> elements.
    /// </summary>
    public sealed class TrecsListHeap
    {
        readonly TrecsLog _log;

        readonly NativeChunkStore _chunkStore;
        readonly HandleTypeRegistry _registry = new();
        bool _isDisposed;

        NativeTrecsListResolver _resolver;

        internal TrecsListHeap(TrecsLog log, NativeChunkStore chunkStore)
        {
            TrecsAssert.IsNotNull(chunkStore);
            _log = log;
            _chunkStore = chunkStore;
            _resolver = new NativeTrecsListResolver(_chunkStore.Resolver);
        }

        public int NumEntries
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _registry.Count;
            }
        }

        public ref NativeTrecsListResolver Resolver
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        public unsafe TrecsList<T> Alloc<T>(int initialCapacity = 0)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(initialCapacity >= 0, "initialCapacity must be non-negative");

            var elementSize = UnsafeUtility.SizeOf<T>();
            var elementAlign = UnsafeUtility.AlignOf<T>();

            // Header in the chunk store — small, stable allocation. The data buffer is
            // also in the chunk store (see AllocDataSlot); the header caches its address
            // in Data for one-indirection job access and stores the owning PtrHandle in
            // DataHandle for grow/free bookkeeping.
            var handle = _chunkStore.Alloc(
                UnsafeUtility.SizeOf<TrecsListHeader>(),
                UnsafeUtility.AlignOf<TrecsListHeader>(),
                TypeHash<T>.Value,
                out var headerAddress
            );

            var headerPtr = (TrecsListHeader*)headerAddress.ToPointer();
            headerPtr->Count = 0;
            headerPtr->Capacity = 0;
            headerPtr->Data = IntPtr.Zero;
            headerPtr->DataHandle = default;
            headerPtr->ElementSize = elementSize;
            headerPtr->ElementAlign = elementAlign;

            if (initialCapacity > 0)
            {
                AllocDataSlot(headerPtr, initialCapacity, TypeHash<T>.Value);
            }

            _registry.Add(handle.Value, typeof(T));

            _log.Trace("Allocated TrecsList<{0}> with handle {1}", typeof(T), handle.Value);

            return new TrecsList<T>(handle);
        }

        /// <summary>
        /// Grows the list's data buffer to hold at least <paramref name="minCapacity"/>
        /// elements. Doubles geometrically past the existing capacity to amortise.
        /// No-op if already at or above <paramref name="minCapacity"/>.
        ///
        /// <para>Main-thread only. Any in-flight job that holds a
        /// <see cref="TrecsListRead{T}"/> or <see cref="TrecsListWrite{T}"/> over this
        /// list must be completed first — verified via the header's
        /// <c>AtomicSafetyHandle</c>.</para>
        /// </summary>
        public unsafe void EnsureCapacity<T>(in TrecsList<T> list, int minCapacity)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(minCapacity >= 0, "minCapacity must be non-negative");

            var entry = ResolveEntry<T>(list.Handle.Value);
            var headerPtr = (TrecsListHeader*)entry.Address.ToPointer();

            if (minCapacity <= headerPtr->Capacity)
            {
                return;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // Reject the grow if any job is still using the list. The cached header->Data
            // is about to change; jobs would otherwise read a dangling pointer.
            AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
#endif

            var newCapacity = headerPtr->Capacity == 0 ? 4 : headerPtr->Capacity;
            while (newCapacity < minCapacity)
            {
                newCapacity *= 2;
            }

            var oldDataHandle = headerPtr->DataHandle;
            var oldData = headerPtr->Data;
            var elementSize = headerPtr->ElementSize;
            var liveBytes = (long)headerPtr->Count * elementSize;

            var newByteSize = newCapacity * elementSize;
            var newDataHandle = _chunkStore.Alloc(
                newByteSize,
                headerPtr->ElementAlign,
                TypeHash<T>.Value,
                out var newDataAddress
            );

            if (liveBytes > 0)
            {
                UnsafeUtility.MemCpy(newDataAddress.ToPointer(), oldData.ToPointer(), liveBytes);
            }
            // No tail MemClear needed — _chunkStore.Alloc already zeroed the slot.

            headerPtr->Data = newDataAddress;
            headerPtr->DataHandle = newDataHandle;
            headerPtr->Capacity = newCapacity;

            if (!oldDataHandle.IsNull)
            {
                _chunkStore.Free(oldDataHandle);
            }
        }

        unsafe void AllocDataSlot(TrecsListHeader* headerPtr, int capacity, int typeHash)
        {
            // _chunkStore.Alloc returns a zeroed slot, so the unused tail past
            // Count*ElementSize is deterministic for snapshots.
            var dataHandle = _chunkStore.Alloc(
                capacity * headerPtr->ElementSize,
                headerPtr->ElementAlign,
                typeHash,
                out var dataAddress
            );
            headerPtr->Data = dataAddress;
            headerPtr->DataHandle = dataHandle;
            headerPtr->Capacity = capacity;
        }

        public unsafe TrecsListRead<T> Read<T>(in TrecsList<T> list)
            where T : unmanaged
        {
            var entry = ResolveEntry<T>(list.Handle.Value);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new TrecsListRead<T>((TrecsListHeader*)entry.Address.ToPointer(), entry.Safety);
#else
            return new TrecsListRead<T>((TrecsListHeader*)entry.Address.ToPointer());
#endif
        }

        public unsafe TrecsListWrite<T> Write<T>(in TrecsList<T> list)
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
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(
                UnityThreadHelper.IsMainThread,
                "TrecsListHeap.ResolveEntry is main-thread only; jobs use NativeTrecsListResolver"
            );
            TrecsAssert.That(address != 0, "Attempted to resolve null TrecsList handle");

            var entry = _chunkStore.ResolveEntry(new PtrHandle(address));
            if (entry.TypeHash != TypeHash<T>.Value)
            {
                throw new TrecsException(
                    $"Type hash mismatch resolving TrecsList<{typeof(T).Name}>: "
                        + $"stored {entry.TypeHash}, requested {TypeHash<T>.Value}"
                );
            }
            return entry;
        }

        public unsafe void DisposeEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(address != 0);

            if (!_registry.ContainsKey(address))
            {
                throw TrecsAssert.CreateException(
                    "Attempted to dispose invalid TrecsList handle ({0})",
                    address
                );
            }

            FreeListBackingSlots(new PtrHandle(address));
            _registry.TryRemove(address);
            _log.Trace("Disposed TrecsList {0}", address);
        }

        /// <summary>
        /// Frees the chunk-store header slot first (its safety handle is the one users
        /// see, so its <c>CheckDeallocateAndThrow</c> catches in-flight jobs), then the
        /// data slot (which has no user-facing safety, so its check passes vacuously).
        /// </summary>
        unsafe void FreeListBackingSlots(PtrHandle headerHandle)
        {
            var headerEntry = _chunkStore.ResolveEntry(headerHandle);
            var dataHandle = ((TrecsListHeader*)headerEntry.Address.ToPointer())->DataHandle;
            _chunkStore.Free(headerHandle);
            if (!dataHandle.IsNull)
            {
                _chunkStore.Free(dataHandle);
            }
        }

        public unsafe void ClearAll(bool warnUndisposed)
        {
            TrecsAssert.That(!_isDisposed);

            if (warnUndisposed && _registry.Count > 0 && _log.IsWarningEnabled())
            {
                _log.Warning(
                    "Found {0} undisposed TrecsLists with element types: {1}",
                    _registry.Count,
                    _registry.DescribeRegisteredTypes()
                );
            }

            // _chunkStore.Free doesn't touch the registry; safe to iterate directly.
            foreach (var address in _registry.Handles)
            {
                FreeListBackingSlots(new PtrHandle(address));
            }
            _registry.Clear();
        }

        internal void Dispose()
        {
            TrecsAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        /// <summary>
        /// Writes managed-side bookkeeping (handle → type) only. The header struct, the
        /// data buffer, and the DataHandle linking them are all dumped by
        /// <c>NativeChunkStore.Serialize</c> which must run before this.
        /// </summary>
        public void Serialize(ISerializationWriter writer)
        {
            TrecsAssert.That(!_isDisposed);
            _registry.Serialize(writer);
            _log.Trace("Serialized {0} TrecsList entries", _registry.Count);
        }

        /// <summary>
        /// Restores managed-side bookkeeping and re-caches each header's <c>Data</c>
        /// pointer from its (restored) <c>DataHandle</c>. Assumes
        /// <c>NativeChunkStore.Deserialize</c> already ran, so both the header and its
        /// data slot are resolvable.
        /// </summary>
        public unsafe void Deserialize(ISerializationReader reader)
        {
            TrecsAssert.That(!_isDisposed);

            _registry.Deserialize(reader);

            // Re-cache header->Data from each restored DataHandle.
            // (Count/Capacity/ElementSize/ElementAlign round-trip via the chunk-store
            // header dump; only the cached data pointer is stale.)
            foreach (var address in _registry.Handles)
            {
                var entry = _chunkStore.ResolveEntry(new PtrHandle(address));
                var headerPtr = (TrecsListHeader*)entry.Address.ToPointer();
                if (!headerPtr->DataHandle.IsNull)
                {
                    var dataEntry = _chunkStore.ResolveEntry(headerPtr->DataHandle);
                    headerPtr->Data = dataEntry.Address;
                }
                else
                {
                    headerPtr->Data = IntPtr.Zero;
                }
            }

            _log.Debug("Deserialized {0} TrecsList entries", _registry.Count);
        }
    }
}
