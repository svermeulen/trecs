using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Frame-scoped heap that stores unmanaged blobs via <see cref="NativeSharedPtr{T}"/>
    /// backed by a <see cref="BlobCache"/>. Entries are tagged with the frame they were
    /// allocated on and can be bulk-cleared by frame range for rollback and replay.
    /// </summary>
    public class FrameScopedNativeSharedHeap
    {
        static readonly TrecsLog _log = new(nameof(FrameScopedNativeSharedHeap));

        readonly DenseDictionary<uint, HeapEntry> _entries = new();
        readonly DenseDictionary<uint, PtrHandle> _blobCacheHandles = new();
        readonly List<uint> _removeBuffer = new();
        readonly BlobCache _store;
        readonly HeapIdCounter _idCounter = new(2, 2);

        bool _isDisposed;

        public FrameScopedNativeSharedHeap(BlobCache store)
        {
            _store = store;
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _entries.Count;
            }
        }

        NativeSharedPtr<T> CreateBlobImpl<T>(int frame, BlobId blobId, PtrHandle blobCacheHandleId)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);

            var id = _idCounter.Alloc();
            _entries.Add(id, new HeapEntry(frame, blobId));
            _blobCacheHandles.Add(id, blobCacheHandleId);
            _log.Trace("Created new input pointer with id {} and type {}", id, typeof(T));
            return new NativeSharedPtr<T>(new PtrHandle(id), blobId);
        }

        internal bool TryGetBlob<T>(int frame, BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);

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
            Assert.That(!_isDisposed);
            Assert.That(_store.HasNativeBlob<T>(blobId, updateAccessTime: true));
            var blobCacheHandleId = _store.CreateHandle(blobId);
            return CreateBlobImpl<T>(frame, blobId, blobCacheHandleId);
        }

        internal NativeSharedPtr<T> CreateBlob<T>(int frame, BlobId blobId, in T blob)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
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
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtrTakingOwnership<T>(
                blobId,
                ptr,
                allocSize,
                allocAlignment
            );
            return CreateBlobImpl<T>(frame, handle.BlobId, handle.Handle);
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            Assert.That(!_isDisposed);

            Assert.That(_removeBuffer.IsEmpty());
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
            Assert.That(!_isDisposed);
            return _entries.ContainsKey(handle);
        }

        // Linear scan is acceptable here: called every frame, but the entry count
        // is typically very small (single digits). A sorted structure would add
        // complexity without measurable benefit in practice.
        internal void ClearAtOrBeforeFrame(int frame)
        {
            Assert.That(!_isDisposed);

            Assert.That(_removeBuffer.IsEmpty());
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
            Assert.That(!_isDisposed);

            if (_entries.TryGetValue(address, out var entry))
            {
                Assert.IsEqual(
                    entry.Frame,
                    frame,
                    "Attempted to get input memory for different frame than it was allocated for"
                );
                return entry;
            }

            throw Assert.CreateException(
                "Attempted to get invalid heap memory address ({}) for frame {}",
                address,
                frame
            );
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }

        internal void ClearAll()
        {
            Assert.That(!_isDisposed);

            foreach (var (_, handleId) in _blobCacheHandles)
            {
                _store.DisposeHandle(handleId);
            }

            _blobCacheHandles.Clear();
            _entries.Clear();
        }

        internal void DisposeEntry(uint address)
        {
            Assert.That(!_isDisposed);

            if (!_entries.TryRemove(address, out var entry))
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid heap memory address ({})",
                    address
                );
            }

            var blobHandle = _blobCacheHandles.RemoveAndGet(address);
            _store.DisposeHandle(blobHandle);
            _log.Trace("Disposed input ptr with address {}", address);
        }

        internal void Serialize(ITrecsSerializationWriter writer)
        {
            Assert.That(!_isDisposed);

            writer.Write<uint>("IdCounter", _idCounter.Value);
            writer.Write<int>("NumEntries", _entries.Count);

            foreach (var (address, entry) in _entries)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("Frame", entry.Frame);
                writer.Write<BlobId>("BlobId", entry.BlobId);
            }
        }

        internal void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(!_isDisposed);

            Assert.That(_entries.Count == 0);

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

                _log.Trace("Deserialized dynamic pointer with id {}", address);
            }

            if (maxAddress > 0)
            {
                _idCounter.AdvancePast(maxAddress);
            }

            _log.Debug("Deserialized {} input heap entries", _entries.Count);
        }

        internal void RemapFrameOffsets(int frameOffset)
        {
            Assert.That(!_isDisposed);

            if (frameOffset == 0)
            {
                return;
            }

            // Collect entries to update (since HeapEntry is readonly)
            var entriesToUpdate = new List<(uint address, HeapEntry entry)>();
            foreach (var (address, entry) in _entries)
            {
                entriesToUpdate.Add((address, entry));
            }

            _entries.Clear();

            foreach (var (address, oldEntry) in entriesToUpdate)
            {
                var newEntry = new HeapEntry(oldEntry.Frame + frameOffset, oldEntry.BlobId);
                _entries.Add(address, newEntry);
            }

            _log.Debug(
                "Remapped {} native blob input heap entries by {} frames",
                entriesToUpdate.Count,
                frameOffset
            );
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
