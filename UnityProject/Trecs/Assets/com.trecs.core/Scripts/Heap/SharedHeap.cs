using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Manages reference-counted managed (class) allocations backing <see cref="SharedPtr{T}"/>.
    /// Accessed internally through <see cref="WorldAccessor"/>; not typically used directly.
    /// </summary>
    public sealed class SharedHeap
    {
        readonly TrecsLog _log;

        readonly BlobCache _store;
        readonly IterableDictionary<BlobId, PtrHandle> _blobCacheHandles = new();
        readonly IterableDictionary<BlobId, BlobInfo> _activeBlobs = new();
        readonly IterableDictionary<PtrHandle, BlobId> _activeHandles = new();
        readonly List<PtrHandle> _tempBuffer1 = new();

        // Skip 0 — PtrHandle reserves 0 as the null sentinel.
        uint _nextHandleId = 1;
        bool _isDisposed;

        public SharedHeap(TrecsLog log, BlobCache store)
        {
            _log = log;
            _store = store;
        }

        // Exposed so WorldAccessor (which already references SharedHeap) can surface
        // the cache without a separate plumbing path. The same instance is shared
        // with NativeSharedHeap and the frame-scoped heaps.
        internal BlobCache BlobCache => _store;

        public int NumEntries
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _activeBlobs.Count;
            }
        }

        public bool CanGetBlob(PtrHandle handle)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                return false;
            }

            return _activeBlobs.ContainsKey(blobId);
        }

        internal bool TryGetBlobDirect<T>(BlobId blobId, PtrHandle handle, out T blob)
            where T : class
        {
            var containsBlob = _activeBlobs.ContainsKey(blobId);
#if TRECS_INTERNAL_CHECKS && DEBUG
            TrecsDebugAssert.That(
                _activeHandles.TryGetValue(handle, out var debugBlobId) == containsBlob,
                "SharedPtr Handle/BlobId mismatch for handle {0} and blobId {1}",
                handle,
                blobId
            );
            TrecsDebugAssert.That(
                !containsBlob || debugBlobId == blobId,
                "SharedPtr Handle maps to different BlobId: expected {0}, got {1}",
                blobId,
                debugBlobId
            );
#endif
            if (containsBlob)
            {
                return _store.TryGetManagedBlob<T>(blobId, out blob, updateAccessTime: true);
            }

            blob = default;
            return false;
        }

        internal bool ContainsBlobDirect(BlobId blobId, PtrHandle handle)
        {
            var containsBlob = _activeBlobs.ContainsKey(blobId);
#if TRECS_INTERNAL_CHECKS && DEBUG
            TrecsDebugAssert.That(
                _activeHandles.TryGetValue(handle, out var debugBlobId) == containsBlob,
                "SharedPtr Handle/BlobId mismatch for handle {0} and blobId {1}",
                handle,
                blobId
            );
            TrecsDebugAssert.That(
                !containsBlob || debugBlobId == blobId,
                "SharedPtr Handle maps to different BlobId: expected {0}, got {1}",
                blobId,
                debugBlobId
            );
#endif
            return containsBlob;
        }

        public bool TryGetBlob<T>(PtrHandle handle, out T blob)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!handle.IsNull);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                blob = default;
                return false;
            }

            if (_activeBlobs.TryGetValue(blobId, out var info))
            {
                TrecsDebugAssert.That(info.TypeHash == TypeId<T>.Value);
                return _store.TryGetManagedBlob<T>(blobId, out blob, updateAccessTime: true);
            }

            blob = default;
            return false;
        }

        public T GetBlob<T>(PtrHandle handle)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!handle.IsNull);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to get an unrecognized blob handle with id {0}",
                    handle.Value
                );
            }

            if (_activeBlobs.TryGetValue(blobId, out var info))
            {
                TrecsDebugAssert.That(info.TypeHash == TypeId<T>.Value);
                return _store.GetManagedBlob<T>(blobId, updateAccessTime: true);
            }

            throw TrecsDebugAssert.CreateException(
                "Attempted to get a disposed blob handle with id {0}",
                blobId
            );
        }

        SharedPtr<T> AddBlobEntry<T>(BlobId blobId, PtrHandle blobCacheHandleId)
            where T : class
        {
            _activeBlobs.Add(blobId, new BlobInfo { RefCount = 0, TypeHash = TypeId<T>.Value });
            _blobCacheHandles.Add(blobId, blobCacheHandleId);
            _log.Trace("Added new blob {0}", blobId);
            return AddBlobHandle<T>(blobId);
        }

        public SharedPtr<T> CreateBlob<T>(BlobId blobId, T blob)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            var handle = _store.AllocManagedBlob<T>(blobId, blob);
            return AddBlobEntry<T>(blobId, handle.Handle);
        }

        public bool TryGetBlob<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_activeBlobs.ContainsKey(blobId))
            {
                ptr = AddBlobHandle<T>(blobId);
                return true;
            }

            if (_store.ContainsManagedBlob<T>(blobId, updateAccessTime: true))
            {
                var blobCacheHandleId = _store.CreateHandle(blobId);
                ptr = AddBlobEntry<T>(blobId, blobCacheHandleId);
                return true;
            }

            ptr = default;
            return false;
        }

        public SharedPtr<T> GetBlob<T>(BlobId blobId)
            where T : class
        {
            if (!TryGetBlob<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to get an unrecognized blob {0}",
                    blobId
                );
            }

            return ptr;
        }

        SharedPtr<T> AddBlobHandle<T>(BlobId blobId)
            where T : class
        {
            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            TrecsDebugAssert.That(info.TypeHash == TypeId<T>.Value);
            info.RefCount += 1;

            var newHandle = new PtrHandle(_nextHandleId++);
            _activeHandles.Add(newHandle, blobId);
            _log.Trace("Added blob handle {0}", newHandle);

            return new SharedPtr<T>(newHandle, blobId);
        }

        public bool TryClone<T>(PtrHandle handle, out SharedPtr<T> result)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                result = default;
                return false;
            }

            result = AddBlobHandle<T>(blobId);
            return true;
        }

        public void ClearAll(bool warnUndisposed)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_activeHandles.Count > 0)
            {
                if (warnUndisposed)
                {
                    if (_activeHandles.Count > 0 && _log.IsWarningEnabled())
                    {
                        var debugStrings = new HashSet<Type>();

                        foreach (var (handle, blobId) in _activeHandles)
                        {
                            debugStrings.Add(
                                _store.GetManagedBlobType(blobId, updateAccessTime: true)
                            );
                        }

                        _log.Warning(
                            "Found {0} managed blob handles that were not disposed, with types: {1}",
                            _activeHandles.Count,
                            debugStrings.Select(x => x.GetPrettyName()).Join(", ")
                        );
                    }
                }

                var removeQueue = _tempBuffer1;
                removeQueue.Clear();

                foreach (var (handle, _) in _activeHandles)
                {
                    removeQueue.Add(handle);
                }

                foreach (var handle in removeQueue)
                {
                    DisposeHandle(handle);
                }

                _tempBuffer1.Clear();
            }

            TrecsDebugAssert.That(_activeBlobs.Count == 0);
            TrecsDebugAssert.That(_activeHandles.Count == 0);
            TrecsDebugAssert.That(_blobCacheHandles.Count == 0);
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        public void DisposeHandle(PtrHandle id)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (!_activeHandles.TryGetValue(id, out var blobId))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to dispose an unrecognized blob handle"
                );
            }

            _activeHandles.RemoveMustExist(id);
            _log.Trace("Disposed blob handle {0}", id);

            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            info.RefCount -= 1;

            TrecsDebugAssert.That(info.RefCount >= 0);

            if (info.RefCount == 0)
            {
                _activeBlobs.RemoveMustExist(blobId);

                var blobHandle = _blobCacheHandles.RemoveAndGet(blobId);
                _store.DisposeHandle(blobHandle);
            }
        }

        public void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(!_isDisposed);
            // Defensive: callers contract is ClearAll() before Deserialize, but
            // a wrong-order call would silently corrupt state — warn-then-clean
            // so the contract violation is observable in dev builds while still
            // recoverable in release.
            ClearAll(warnUndisposed: true);

            _nextHandleId = reader.Read<uint>("HandleCounter");
            reader.ReadInPlace<IterableDictionary<BlobId, BlobInfo>>("_activeBlobs", _activeBlobs);
            reader.ReadInPlace<IterableDictionary<PtrHandle, BlobId>>(
                "_activeHandles",
                _activeHandles
            );

            foreach (var (blobId, _) in _activeBlobs)
            {
                _blobCacheHandles.Add(blobId, _store.CreateHandle(blobId));
            }
        }

        public void Serialize(ISerializationWriter writer)
        {
            writer.Write<uint>("HandleCounter", _nextHandleId);
            writer.Write<IterableDictionary<BlobId, BlobInfo>>("_activeBlobs", _activeBlobs);
            writer.Write<IterableDictionary<PtrHandle, BlobId>>("_activeHandles", _activeHandles);
        }

        public struct BlobInfo
        {
            public int RefCount;
            public TypeId TypeHash;
        }
    }
}
