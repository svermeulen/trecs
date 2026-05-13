using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Manages reference-counted managed (class) allocations backing <see cref="SharedPtr{T}"/>.
    /// Accessed internally through <see cref="HeapAccessor"/>; not typically used directly.
    /// </summary>
    public sealed class SharedHeap
    {
        readonly TrecsLog _log;

        readonly BlobCache _store;
        readonly DenseDictionary<BlobId, PtrHandle> _blobCacheHandles = new();
        readonly DenseDictionary<BlobId, BlobInfo> _activeBlobs = new();
        readonly DenseDictionary<PtrHandle, BlobId> _activeHandles = new();
        readonly List<PtrHandle> _tempBuffer1 = new();

        readonly HeapIdCounter _idCounter = new(1, 2);
        bool _isDisposed;

        public SharedHeap(TrecsLog log, BlobCache store)
        {
            _log = log;
            _store = store;
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _activeBlobs.Count;
            }
        }

        public bool CanGetBlob(PtrHandle handle)
        {
            Assert.That(!_isDisposed);

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
            Assert.That(
                _activeHandles.TryGetValue(handle, out var debugBlobId) == containsBlob,
                "SharedPtr Handle/BlobId mismatch for handle {} and blobId {}",
                handle,
                blobId
            );
            Assert.That(
                !containsBlob || debugBlobId == blobId,
                "SharedPtr Handle maps to different BlobId: expected {}, got {}",
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
            Assert.That(
                _activeHandles.TryGetValue(handle, out var debugBlobId) == containsBlob,
                "SharedPtr Handle/BlobId mismatch for handle {} and blobId {}",
                handle,
                blobId
            );
            Assert.That(
                !containsBlob || debugBlobId == blobId,
                "SharedPtr Handle maps to different BlobId: expected {}, got {}",
                blobId,
                debugBlobId
            );
#endif
            return containsBlob;
        }

        public bool TryGetBlob<T>(PtrHandle handle, out T blob)
            where T : class
        {
            Assert.That(!_isDisposed);
            Assert.That(!handle.IsNull);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                blob = default;
                return false;
            }

            if (_activeBlobs.TryGetValue(blobId, out var info))
            {
                Assert.That(info.TypeHash == TypeIdProvider.GetTypeId<T>());
                return _store.TryGetManagedBlob<T>(blobId, out blob, updateAccessTime: true);
            }

            blob = default;
            return false;
        }

        public T GetBlob<T>(PtrHandle handle)
            where T : class
        {
            Assert.That(!_isDisposed);
            Assert.That(!handle.IsNull);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                throw Assert.CreateException(
                    "Attempted to get an unrecognized blob handle with id {}",
                    handle.Value
                );
            }

            if (_activeBlobs.TryGetValue(blobId, out var info))
            {
                Assert.That(info.TypeHash == TypeIdProvider.GetTypeId<T>());
                return _store.GetManagedBlob<T>(blobId, updateAccessTime: true);
            }

            throw Assert.CreateException(
                "Attempted to get a disposed blob handle with id {}",
                blobId
            );
        }

        SharedPtr<T> AddBlobEntry<T>(BlobId blobId, PtrHandle blobCacheHandleId)
            where T : class
        {
            _activeBlobs.Add(
                blobId,
                new BlobInfo { RefCount = 0, TypeHash = TypeIdProvider.GetTypeId<T>() }
            );
            _blobCacheHandles.Add(blobId, blobCacheHandleId);
            _log.Trace("Added new blob {0}", blobId);
            return AddBlobHandle<T>(blobId);
        }

        public SharedPtr<T> CreateBlob<T>(BlobId blobId, T blob)
            where T : class
        {
            Assert.That(!_isDisposed);
            var handle = _store.CreateBlobPtr<T>(blobId, blob);
            return AddBlobEntry<T>(blobId, handle.Handle);
        }

        public bool TryGetBlob<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            Assert.That(!_isDisposed);

            if (_activeBlobs.ContainsKey(blobId))
            {
                ptr = AddBlobHandle<T>(blobId);
                return true;
            }

            if (_store.HasManagedBlob<T>(blobId, updateAccessTime: true))
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
                throw Assert.CreateException("Attempted to get an unrecognized blob {}", blobId);
            }

            return ptr;
        }

        SharedPtr<T> AddBlobHandle<T>(BlobId blobId)
            where T : class
        {
            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            Assert.That(info.TypeHash == TypeIdProvider.GetTypeId<T>());
            info.RefCount += 1;

            var newHandle = new PtrHandle(_idCounter.Alloc());
            _activeHandles.Add(newHandle, blobId);
            _log.Trace("Added blob handle {0}", newHandle);

            return new SharedPtr<T>(newHandle, blobId);
        }

        public bool TryClone<T>(PtrHandle handle, out SharedPtr<T> result)
            where T : class
        {
            Assert.That(!_isDisposed);

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
            Assert.That(!_isDisposed);

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

            Assert.That(_activeBlobs.Count == 0);
            Assert.That(_activeHandles.Count == 0);
            Assert.That(_blobCacheHandles.Count == 0);
        }

        public void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        public void DisposeHandle(PtrHandle id)
        {
            Assert.That(!_isDisposed);

            if (!_activeHandles.TryGetValue(id, out var blobId))
            {
                throw Assert.CreateException("Attempted to dispose an unrecognized blob handle");
            }

            _activeHandles.RemoveMustExist(id);
            _log.Trace("Disposed blob handle {0}", id);

            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            info.RefCount -= 1;

            Assert.That(info.RefCount >= 0);

            if (info.RefCount == 0)
            {
                _activeBlobs.RemoveMustExist(blobId);

                var blobHandle = _blobCacheHandles.RemoveAndGet(blobId);
                _store.DisposeHandle(blobHandle);
            }
        }

        public void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(_activeBlobs.Count == 0);
            Assert.That(_activeHandles.Count == 0);

            _idCounter.Value = reader.Read<uint>("_handleCounter");
            reader.ReadInPlace<DenseDictionary<BlobId, BlobInfo>>("_activeBlobs", _activeBlobs);
            reader.ReadInPlace<DenseDictionary<PtrHandle, BlobId>>(
                "_activeHandles",
                _activeHandles
            );

            foreach (var (blobId, _) in _activeBlobs)
            {
                _blobCacheHandles.Add(blobId, _store.CreateHandle(blobId));
            }
        }

        public void Serialize(ITrecsSerializationWriter writer)
        {
            writer.Write<uint>("_handleCounter", _idCounter.Value);
            writer.Write<DenseDictionary<BlobId, BlobInfo>>("_activeBlobs", _activeBlobs);
            writer.Write<DenseDictionary<PtrHandle, BlobId>>("_activeHandles", _activeHandles);
        }

        public struct BlobInfo
        {
            public int RefCount;
            public int TypeHash;
        }
    }
}
