using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Managed-side analog of <see cref="InputNativeSharedHeap"/>. Tracks
    /// refcount handles into the shared <see cref="BlobCache"/> for managed
    /// blobs allocated through the input pipeline
    /// (<see cref="InputSharedPtr{T}"/>). Releases handles in bulk when a
    /// frame is trimmed; per-frame <see cref="List{PtrHandle}"/>s are pooled.
    /// </summary>
    public sealed class InputSharedHeap
    {
        readonly TrecsLog _log;
        readonly BlobCache _store;

        // (frame -> list of (BlobId, refcount handle)). BlobId is needed to
        // recreate the refcount handle on Deserialize; the handle itself is
        // used only for Release on frame trim.
        readonly IterableDictionary<int, List<Entry>> _entriesByFrame = new();
        readonly Stack<List<Entry>> _listPool = new();
        readonly List<int> _frameRemoveBuffer = new();

        bool _isDisposed;

        readonly struct Entry
        {
            public readonly BlobId BlobId;
            public readonly PtrHandle CacheHandle;

            public Entry(BlobId blobId, PtrHandle cacheHandle)
            {
                BlobId = blobId;
                CacheHandle = cacheHandle;
            }
        }

        public InputSharedHeap(TrecsLog log, BlobCache store)
        {
            _log = log;
            _store = store;
        }

        public int NumLiveFrames
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _entriesByFrame.Count;
            }
        }

        internal InputSharedPtr<T> Alloc<T>(int frame, BlobId blobId, T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            var handle = _store.AllocManagedBlob<T>(blobId, value);
            TrackEntry(frame, handle.BlobId, handle.Handle);
            _log.Trace(
                "Allocated input managed shared type={0} blobId={1} frame={2}",
                typeof(T),
                handle.BlobId,
                frame
            );
            return new InputSharedPtr<T>(handle.BlobId);
        }

        internal bool TryAcquire<T>(int frame, BlobId blobId, out InputSharedPtr<T> ptr)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            if (!_store.ContainsManagedBlob<T>(blobId, updateAccessTime: true))
            {
                ptr = default;
                return false;
            }
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle);
            ptr = new InputSharedPtr<T>(blobId);
            return true;
        }

        internal InputSharedPtr<T> Acquire<T>(int frame, BlobId blobId)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(
                _store.ContainsManagedBlob<T>(blobId, updateAccessTime: false),
                "Acquire: no managed blob exists at BlobId {0}",
                blobId
            );
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle);
            return new InputSharedPtr<T>(blobId);
        }

        void TrackEntry(int frame, BlobId blobId, PtrHandle cacheHandle)
        {
            if (!_entriesByFrame.TryGetValue(frame, out var list))
            {
                list = _listPool.Count > 0 ? _listPool.Pop() : new List<Entry>();
                TrecsDebugAssert.That(list.Count == 0);
                _entriesByFrame.Add(frame, list);
            }
            list.Add(new Entry(blobId, cacheHandle));
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _entriesByFrame)
            {
                if (f >= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAtOrBeforeFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _entriesByFrame)
            {
                if (f <= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAll()
        {
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var (_, list) in _entriesByFrame)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    _store.DisposeHandle(list[i].CacheHandle);
                }
                list.Clear();
                _listPool.Push(list);
            }
            _entriesByFrame.Clear();
        }

        void ReleaseFrame(int frame)
        {
            var list = _entriesByFrame.RemoveAndGet(frame);
            for (int i = 0; i < list.Count; i++)
            {
                _store.DisposeHandle(list[i].CacheHandle);
            }
            list.Clear();
            _listPool.Push(list);
        }

        /// <summary>
        /// Writes (frame -> [BlobId, ...]) pairs. The refcount handle isn't
        /// serialized — it's re-minted from the BlobId on Deserialize via
        /// <see cref="BlobCache.CreateHandle"/>.
        /// </summary>
        internal void Serialize(ISerializationWriter writer)
        {
            TrecsDebugAssert.That(!_isDisposed);

            writer.Write<int>("NumFrames", _entriesByFrame.Count);
            foreach (var (frame, list) in _entriesByFrame)
            {
                writer.Write<int>("Frame", frame);
                writer.Write<int>("NumEntries", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    writer.Write<BlobId>("BlobId", list[i].BlobId);
                }
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(!_isDisposed);
            // Defensive: callers contract is ClearAll() before Deserialize.
            ClearAll();

            var numFrames = reader.Read<int>("NumFrames");
            for (int i = 0; i < numFrames; i++)
            {
                var frame = reader.Read<int>("Frame");
                var numEntries = reader.Read<int>("NumEntries");
                for (int k = 0; k < numEntries; k++)
                {
                    var blobId = reader.Read<BlobId>("BlobId");
                    var handle = _store.CreateHandle(blobId);
                    TrackEntry(frame, blobId, handle);
                }
            }
            _log.Debug("Deserialized {0} frames into InputSharedHeap", _entriesByFrame.Count);
        }

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }
    }
}
