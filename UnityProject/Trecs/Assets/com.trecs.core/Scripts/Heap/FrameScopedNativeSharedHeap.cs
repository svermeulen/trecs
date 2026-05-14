using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Frame-scoped heap that stores unmanaged blobs via <see cref="NativeSharedPtr{T}"/>
    /// backed by a <see cref="BlobCache"/>. Entries are tagged with the frame they were
    /// allocated on and can be bulk-cleared by frame range for rollback and replay.
    /// </summary>
    public sealed class FrameScopedNativeSharedHeap
    {
        readonly TrecsLog _log;

        readonly DenseDictionary<uint, HeapEntry> _entries = new();
        readonly DenseDictionary<uint, PtrHandle> _blobCacheHandles = new();
        readonly List<uint> _removeBuffer = new();
        readonly BlobCache _store;
        readonly HeapIdCounter _idCounter = new(2, 2);

        bool _isDisposed;

        public FrameScopedNativeSharedHeap(TrecsLog log, BlobCache store)
        {
            _log = log;
            _store = store;
        }

        public int NumEntries
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _entries.Count;
            }
        }

        NativeSharedPtr<T> CreateBlobImpl<T>(int frame, BlobId blobId, PtrHandle blobCacheHandleId)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);

            var id = _idCounter.Alloc();
            _entries.Add(id, new HeapEntry(frame, blobId));
            _blobCacheHandles.Add(id, blobCacheHandleId);
            _log.Trace("Created new input pointer with id {0} and type {1}", id, typeof(T));
            return new NativeSharedPtr<T>(new PtrHandle(id), blobId);
        }

        internal bool TryGetBlob<T>(int frame, BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);

            if (!_store.HasNativeBlob<T>(blobId, updateAccessTime: true))
            {
                ptr = default;
                return false;
            }

            var blobCacheHandleId = _store.CreateHandle(blobId);
            ptr = CreateBlobImpl<T>(frame, blobId, blobCacheHandleId);
            return true;
        }

        internal NativeSharedPtr<T> CreateBlob<T>(int frame, BlobId blobId)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(_store.HasNativeBlob<T>(blobId, updateAccessTime: false));
            var blobCacheHandleId = _store.CreateHandle(blobId);
            return CreateBlobImpl<T>(frame, blobId, blobCacheHandleId);
        }

        internal NativeSharedPtr<T> CreateBlob<T>(int frame, BlobId blobId, in T blob)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtr(blobId, in blob);
            return CreateBlobImpl<T>(frame, handle.BlobId, handle.Handle);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer.
        /// See <see cref="NativeUniqueHeap.AllocTakingOwnership{T}"/> for the contract.
        /// </summary>
        internal NativeSharedPtr<T> CreateBlobTakingOwnership<T>(
            int frame,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtrTakingOwnership<T>(blobId, alloc);
            return CreateBlobImpl<T>(frame, handle.BlobId, handle.Handle);
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_removeBuffer.IsEmpty());
            _removeBuffer.Clear();

            foreach (var (key, value) in _entries)
            {
                if (value.Frame >= frame)
                {
                    _removeBuffer.Add(key);
                }
            }

            foreach (var key in _removeBuffer)
            {
                DisposeEntry(key);
            }

            _removeBuffer.Clear();
        }

        internal BlobId GetBlobId(int frame, uint address)
        {
            var entry = GetEntry(frame, address);
            return entry.BlobId;
        }

        internal bool ContainsEntry(uint handle)
        {
            TrecsAssert.That(!_isDisposed);
            return _entries.ContainsKey(handle);
        }

        // Linear scan is acceptable here: called every frame, but the entry count
        // is typically very small (single digits). A sorted structure would add
        // complexity without measurable benefit in practice.
        internal void ClearAtOrBeforeFrame(int frame)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_removeBuffer.IsEmpty());
            _removeBuffer.Clear();

            foreach (var (key, value) in _entries)
            {
                if (value.Frame <= frame)
                {
                    _removeBuffer.Add(key);
                }
            }

            foreach (var key in _removeBuffer)
            {
                DisposeEntry(key);
            }

            _removeBuffer.Clear();
        }

        HeapEntry GetEntry(int frame, uint address)
        {
            TrecsAssert.That(!_isDisposed);

            if (_entries.TryGetValue(address, out var entry))
            {
                TrecsAssert.IsEqual(
                    entry.Frame,
                    frame,
                    "Attempted to get input memory for different frame than it was allocated for"
                );
                return entry;
            }

            throw TrecsAssert.CreateException(
                "Attempted to get invalid heap memory address ({0}) for frame {1}",
                address,
                frame
            );
        }

        internal void Dispose()
        {
            TrecsAssert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }

        internal void ClearAll()
        {
            TrecsAssert.That(!_isDisposed);

            foreach (var (_, handleId) in _blobCacheHandles)
            {
                _store.DisposeHandle(handleId);
            }

            _blobCacheHandles.Clear();
            _entries.Clear();
        }

        internal void DisposeEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);

            if (!_entries.TryRemove(address, out var entry))
            {
                throw TrecsAssert.CreateException(
                    "Attempted to dispose invalid heap memory address ({0})",
                    address
                );
            }

            var blobHandle = _blobCacheHandles.RemoveAndGet(address);
            _store.DisposeHandle(blobHandle);
            _log.Trace("Disposed input ptr with address {0}", address);
        }

        internal void Serialize(ISerializationWriter writer)
        {
            TrecsAssert.That(!_isDisposed);

            writer.Write<uint>("IdCounter", _idCounter.Value);
            writer.Write<int>("NumEntries", _entries.Count);

            foreach (var (address, entry) in _entries)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("Frame", entry.Frame);
                writer.Write<BlobId>("BlobId", entry.BlobId);
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_entries.Count == 0);

            // See FrameScopedSharedHeap.Deserialize for the rationale behind EnsureAtLeast.
            _idCounter.EnsureAtLeast(reader.Read<uint>("IdCounter"));
            var numEntries = reader.Read<int>("NumEntries");

            _entries.EnsureCapacity(numEntries);

            uint maxAddress = 0;

            for (int i = 0; i < numEntries; i++)
            {
                var address = reader.Read<uint>("Address");
                if (address > maxAddress)
                {
                    maxAddress = address;
                }

                var frame = reader.Read<int>("Frame");
                var blobId = reader.Read<BlobId>("BlobId");

                _entries.Add(address, new HeapEntry(frame, blobId));
                _blobCacheHandles.Add(address, _store.CreateHandle(blobId));

                _log.Trace("Deserialized dynamic pointer with id {0}", address);
            }

            if (maxAddress > 0)
            {
                _idCounter.AdvancePast(maxAddress);
            }

            _log.Debug("Deserialized {0} input heap entries", _entries.Count);
        }

        readonly struct HeapEntry
        {
            public readonly BlobId BlobId;
            public readonly int Frame;

            public HeapEntry(int frame, BlobId blobId)
            {
                Frame = frame;
                BlobId = blobId;
            }
        }
    }
}
