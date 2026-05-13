using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Manages the storage backing <see cref="TrecsList{T}"/>. The stable
    /// <see cref="TrecsListHeader"/> for each list is allocated through the shared
    /// <see cref="NativeChunkStore"/> (so wrappers can cache the header pointer across grows
    /// and the safety handle is minted alongside every other native-heap allocation).
    /// The resizable data buffer stays as a direct <c>AllocatorManager.Persistent</c>
    /// allocation owned by the header — necessary because the wrapper's <c>Grow</c> path runs
    /// inside Burst jobs and needs allocator calls that Burst can compile.
    /// </summary>
    public sealed class TrecsListHeap
    {
        static readonly TrecsLog _log = new(nameof(TrecsListHeap));

        readonly NativeChunkStore _chunkStore;
        readonly Dictionary<uint, Type> _typesByHandle = new();
        bool _isDisposed;

        NativeTrecsListResolver _resolver;

        // Static delegate so chunk_store.Free's onDrained callback doesn't allocate a
        // closure per DisposeEntry call. Reads the data buffer info out of the header
        // via the entry's still-valid Address.
        static readonly Action<NativeChunkStoreEntry> s_freeDataBufferOnDrained =
            FreeDataBufferOnDrained;

        public TrecsListHeap(NativeChunkStore chunkStore)
        {
            Assert.IsNotNull(chunkStore);
            _chunkStore = chunkStore;
            _resolver = new NativeTrecsListResolver(_chunkStore.Resolver);
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _typesByHandle.Count;
            }
        }

        public ref NativeTrecsListResolver Resolver
        {
            get
            {
                Assert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        public unsafe TrecsList<T> Alloc<T>(int initialCapacity = 0)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(initialCapacity >= 0, "initialCapacity must be non-negative");

            var elementSize = UnsafeUtility.SizeOf<T>();
            var elementAlign = UnsafeUtility.AlignOf<T>();

            // Header in the chunk store — small, stable allocation. The data buffer is
            // owned by the header (header.Data) and managed by AllocatorManager directly.
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
            headerPtr->ElementSize = elementSize;
            headerPtr->ElementAlign = elementAlign;

            if (initialCapacity > 0)
            {
                var data = AllocatorManager.Allocate(
                    Allocator.Persistent,
                    elementSize,
                    elementAlign,
                    initialCapacity
                );
                headerPtr->Data = new IntPtr(data);
                headerPtr->Capacity = initialCapacity;
            }

            _typesByHandle.Add(handle.Value, typeof(T));

            _log.Trace("Allocated TrecsList<{}> with handle {}", typeof(T), handle.Value);

            return new TrecsList<T>(handle);
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
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "TrecsListHeap.ResolveEntry is main-thread only; jobs use NativeTrecsListResolver"
            );
            Assert.That(address != 0, "Attempted to resolve null TrecsList handle");

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

        public void DisposeEntry(uint address)
        {
            Assert.That(!_isDisposed);
            Assert.That(address != 0);

            if (!_typesByHandle.Remove(address))
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid TrecsList handle ({})",
                    address
                );
            }

            // Free the chunk-store header AND the AllocatorManager-owned data buffer.
            // s_freeDataBufferOnDrained reads the data pointer out of the header — it runs
            // after the safety handle has been drained but before the header slot is
            // released, so no Burst job can be reading header->Data when we free it.
            _chunkStore.Free(new PtrHandle(address), s_freeDataBufferOnDrained);
            _log.Trace("Disposed TrecsList {}", address);
        }

        static unsafe void FreeDataBufferOnDrained(NativeChunkStoreEntry entry)
        {
            var headerPtr = (TrecsListHeader*)entry.Address.ToPointer();
            if (headerPtr->Data != IntPtr.Zero)
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    headerPtr->Data.ToPointer(),
                    headerPtr->ElementSize,
                    headerPtr->ElementAlign,
                    headerPtr->Capacity
                );
            }
        }

        internal void FlushPendingOperations()
        {
            Assert.That(!_isDisposed);
            _chunkStore.FlushPendingOperations();
        }

        public void ClearAll(bool warnUndisposed)
        {
            Assert.That(!_isDisposed);

            if (warnUndisposed && _typesByHandle.Count > 0 && _log.IsWarningEnabled())
            {
                var typeNames = _typesByHandle
                    .Values.Select(t => t.GetPrettyName())
                    .Distinct()
                    .Join(", ");
                _log.Warning(
                    "Found {} undisposed TrecsLists with element types: {}",
                    _typesByHandle.Count,
                    typeNames
                );
            }

            foreach (var address in _typesByHandle.Keys.ToArray())
            {
                _chunkStore.Free(new PtrHandle(address), s_freeDataBufferOnDrained);
            }
            _typesByHandle.Clear();
            _chunkStore.FlushPendingOperations();
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        public unsafe void Serialize(ITrecsSerializationWriter writer)
        {
            Assert.That(!_isDisposed);
            FlushPendingOperations();

            writer.Write<int>("NumEntries", _typesByHandle.Count);

            foreach (var (address, type) in _typesByHandle)
            {
                var entry = _chunkStore.ResolveEntry(new PtrHandle(address));
                var headerPtr = (TrecsListHeader*)entry.Address.ToPointer();

                writer.Write<uint>("Address", address);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(type));
                writer.Write<int>("Count", headerPtr->Count);
                writer.Write<int>("Capacity", headerPtr->Capacity);
                writer.Write<int>("ElementSize", headerPtr->ElementSize);
                writer.Write<int>("ElementAlign", headerPtr->ElementAlign);

                // Only the live portion (Count * ElementSize) is meaningful; the rest of the
                // buffer (up to Capacity) is uninitialised tail space.
                var liveBytes = headerPtr->Count * headerPtr->ElementSize;
                if (liveBytes > 0)
                {
                    writer.BlitWriteRawBytes("Data", headerPtr->Data.ToPointer(), liveBytes);
                }
            }

            _log.Trace("Serialized {} TrecsList entries", _typesByHandle.Count);
        }

        public unsafe void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(!_isDisposed);
            Assert.That(_typesByHandle.Count == 0);

            var numEntries = reader.Read<int>("NumEntries");

            for (int i = 0; i < numEntries; i++)
            {
                var savedAddress = reader.Read<uint>("Address");
                var innerTypeId = reader.Read<int>("InnerTypeId");
                var count = reader.Read<int>("Count");
                var capacity = reader.Read<int>("Capacity");
                var elementSize = reader.Read<int>("ElementSize");
                var elementAlign = reader.Read<int>("ElementAlign");
                var innerType = TypeIdProvider.GetTypeFromId(innerTypeId);

                // Restore the header slot at its original handle so component-stored
                // TrecsList handles still resolve.
                var handle = _chunkStore.AllocAtSlot(
                    savedAddress,
                    UnsafeUtility.SizeOf<TrecsListHeader>(),
                    UnsafeUtility.AlignOf<TrecsListHeader>(),
                    BurstRuntime.GetHashCode32(innerType),
                    out var headerAddress
                );
                var headerPtr = (TrecsListHeader*)headerAddress.ToPointer();

                IntPtr dataPtr = IntPtr.Zero;
                if (capacity > 0)
                {
                    dataPtr = new IntPtr(
                        AllocatorManager.Allocate(
                            Allocator.Persistent,
                            elementSize,
                            elementAlign,
                            capacity
                        )
                    );
                    var liveBytes = count * elementSize;
                    if (liveBytes > 0)
                    {
                        reader.BlitReadRawBytes("Data", dataPtr.ToPointer(), liveBytes);
                    }
                }

                headerPtr->Count = count;
                headerPtr->Capacity = capacity;
                headerPtr->ElementSize = elementSize;
                headerPtr->ElementAlign = elementAlign;
                headerPtr->Data = dataPtr;

                _typesByHandle.Add(handle.Value, innerType);
            }

            _chunkStore.OnDeserializeComplete();
            _log.Debug("Deserialized {} TrecsList entries", numEntries);
        }
    }
}
