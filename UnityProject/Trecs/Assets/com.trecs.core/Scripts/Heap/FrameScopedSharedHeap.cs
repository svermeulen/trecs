using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Frame-scoped heap that stores managed blobs via <see cref="SharedPtr{T}"/> backed
    /// by a <see cref="BlobCache"/>. Entries are tagged with the frame they were allocated
    /// on and can be bulk-cleared by frame range for rollback and replay.
    /// </summary>
    public sealed class FrameScopedSharedHeap
    {
        readonly TrecsLog _log;

        readonly DenseDictionary<uint, HeapEntry> _entries = new();
        readonly DenseDictionary<uint, PtrHandle> _blobCacheHandles = new();
        readonly List<uint> _removeBuffer = new();
        readonly BlobCache _store;
        readonly HeapIdCounter _idCounter = new(2, 2);

        bool _isDisposed;

        public FrameScopedSharedHeap(TrecsLog log, BlobCache store)
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

        SharedPtr<T> CreateBlobImpl<T>(int frame, BlobId blobId, PtrHandle blobCacheHandleId)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);

            var id = _idCounter.Alloc();
            _entries.Add(id, new HeapEntry(frame, blobId));
            _blobCacheHandles.Add(id, blobCacheHandleId);
            _log.Trace("Allocated new input pointer with id {0} and type {1}", id, typeof(T));
            return new SharedPtr<T>(new PtrHandle(id), blobId);
        }

        internal bool TryGetBlob<T>(int frame, BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);

            if (!_store.HasManagedBlob<T>(blobId, updateAccessTime: true))
            {
                ptr = default;
                return false;
            }

            var blobCacheHandleId = _store.CreateHandle(blobId);
            ptr = CreateBlobImpl<T>(frame, blobId, blobCacheHandleId);
            return true;
        }

        internal SharedPtr<T> CreateBlob<T>(int frame, BlobId blobId)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(_store.HasManagedBlob<T>(blobId, updateAccessTime: false));
            var blobCacheHandleId = _store.CreateHandle(blobId);
            return CreateBlobImpl<T>(frame, blobId, blobCacheHandleId);
        }

        internal SharedPtr<T> CreateBlob<T>(int frame, BlobId blobId, T blob)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            var handle = _store.CreateBlobPtr<T>(blobId, blob);
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

        internal BlobId GetBlobId(int frame, uint address)
        {
            var entry = GetEntry(frame, address);
            return entry.BlobId;
        }

        internal T ResolveValue<T>(int frame, uint address)
            where T : class
        {
            var entry = GetEntry(frame, address);
            return _store.GetManagedBlob<T>(entry.BlobId, updateAccessTime: true);
        }

        internal bool TryResolveValue<T>(int frame, uint handle, out T value)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);

            if (_entries.TryGetValue(handle, out var entry))
            {
                TrecsAssert.IsEqual(
                    entry.Frame,
                    frame,
                    "Attempted to get input memory for different frame than it was allocated for"
                );
                value = _store.GetManagedBlob<T>(entry.BlobId, updateAccessTime: true);
                return true;
            }

            value = default;
            return false;
        }

        internal bool ContainsEntry(uint handle)
        {
            TrecsAssert.That(!_isDisposed);
            return _entries.ContainsKey(handle);
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

            // IdCounter handling for frame-scoped heaps is subtle. There are two scenarios:
            //
            //   1) Full state restore: the EntityInputQueue is being deserialized as part
            //      of loading a recording bundle whose initial snapshot has already wiped
            //      world state. _idCounter is at its initial value. We want to take the
            //      saved value to preserve replay determinism — subsequent allocations must
            //      produce the same address sequence as the original run.
            //
            //   2) Deserialize-on-top-of-running: the EntityInputQueue is being replaced
            //      while the game is RUNNING (e.g. BundlePlayer.Start swaps the input
            //      queue contents after restoring the bundle's snapshot, but the input
            //      queue itself is not part of the world snapshot — ClearAllInputs wiped
            //      entries but deliberately did NOT reset _idCounter, so it holds whatever
            //      value the running game has reached). We must NOT clobber it with a
            //      smaller recorded value, or future allocations would collide with the
            //      freshly loaded recorded entries.
            //
            // EnsureAtLeast(savedValue) handles both cases:
            //   - In case 1, current is small (initial), saved is large → use saved.
            //   - In case 2, current is large (running game), saved is small → keep current.
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

            // Defense in depth: if the saved IdCounter was somehow lower than max entry
            // address (corrupt file), bump past it.
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
