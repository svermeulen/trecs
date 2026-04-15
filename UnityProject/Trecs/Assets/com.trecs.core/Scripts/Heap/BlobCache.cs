using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Trecs
{
    public class BlobCacheSettings
    {
        public float CleanIntervalSeconds;
        public int SerializationVersion;

        public static BlobCacheSettings Default = new()
        {
            CleanIntervalSeconds = 5,
            SerializationVersion = 1,
        };
    }

    public enum BlobLoadingState
    {
        NotLoaded,
        Loading,
        Loaded,
    }

    public class BlobCache
    {
        static readonly TrecsLog _log = new(nameof(BlobCache));

        readonly DenseHashSet<BlobId> _cleanBuffer1 = new();
        readonly Rng _rng = new();
        readonly DenseDictionary<PtrHandle, BlobId> _handles = new();
        readonly BlobCacheSettings _settings;
        readonly List<IBlobStore> _stores;
        readonly IBlobStore _writableStore;

        uint _handleIdCounter = 1;
        bool _hasDisposed;
        float _cleanCountdown;

        public BlobCache(List<IBlobStore> stores, BlobCacheSettings settings)
        {
            _stores = stores;
            _settings = settings ?? BlobCacheSettings.Default;

            foreach (var store in stores)
            {
                store.SerializationVersion = _settings.SerializationVersion;

                if (!store.IsReadOnly)
                {
                    Assert.IsNull(_writableStore);
                    _writableStore = store;
                }
            }
        }

        public IReadOnlyList<IBlobStore> BlobStores
        {
            get { return _stores; }
        }

        public IBlobStore WritableBlobStore
        {
            get { return RequireWritableStore(); }
        }

        IBlobStore RequireWritableStore()
        {
            Assert.IsNotNull(_writableStore, "No writable blob store found");
            return _writableStore;
        }

        public void Tick()
        {
            Assert.That(!_hasDisposed);

            _cleanCountdown -= Time.deltaTime;

            // _dbg.Text("Num Handles: {}", _handles.Count);

            if (_cleanCountdown <= 0)
            {
                _cleanCountdown = _settings.CleanIntervalSeconds;

                CleanCaches();
            }
        }

        bool TryGetManifestEntry(BlobId id, out BlobMetadata manifestEntry, bool updateAccessTime)
        {
            Assert.That(!_hasDisposed);

            foreach (var store in _stores)
            {
                if (
                    store.TryGetManifestEntry(
                        id,
                        out manifestEntry,
                        updateAccessTime: updateAccessTime
                    )
                )
                {
                    return true;
                }
            }

            manifestEntry = default;
            return false;
        }

        internal Type TryGetManagedBlobType(BlobId id, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime))
            {
                Assert.That(!manifestEntry.IsNative);
                Assert.That(manifestEntry.Type.IsClass);
                return manifestEntry.Type;
            }

            return null;
        }

        internal Type GetManagedBlobType(BlobId id, bool updateAccessTime = true)
        {
            var result = TryGetManagedBlobType(id, updateAccessTime);
            Assert.IsNotNull(result);
            return result;
        }

        internal Type TryGetNativeBlobType(BlobId id, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime))
            {
                Assert.That(manifestEntry.IsNative);
                return manifestEntry.Type;
            }

            return null;
        }

        internal Type GetNativeBlobType(BlobId id, bool updateAccessTime = true)
        {
            var result = TryGetNativeBlobType(id, updateAccessTime);
            Assert.IsNotNull(result);
            return result;
        }

        public void GetAllActiveBlobIds(DenseHashSet<BlobId> blobIds)
        {
            foreach (var (_, blobId) in _handles)
            {
                blobIds.Add(blobId);
            }
        }

        void CleanCaches()
        {
            var activeBlobs = _cleanBuffer1;
            activeBlobs.Clear();
            GetAllActiveBlobIds(activeBlobs);

            // _dbg.Text(
            //     displayTime: _settings.CleanIntervalSeconds,
            //     "Num Active Blobs: {}",
            //     activeBlobs.Count
            // );

            foreach (var store in _stores)
            {
                store.CleanCache(activeBlobs);
            }
        }

        public bool HasBlob(BlobId id, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);
            return TryGetManifestEntry(id, out var _, updateAccessTime: updateAccessTime);
        }

        public bool HasManagedBlob(BlobId id, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                Assert.That(!manifestEntry.IsNative);
                return true;
            }

            return false;
        }

        public bool HasManagedBlob<T>(BlobId id, bool updateAccessTime = true)
            where T : class
        {
            Assert.That(!_hasDisposed);

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                return !manifestEntry.IsNative && typeof(T).IsAssignableFrom(manifestEntry.Type);
            }

            return false;
        }

        public bool HasNativeBlob(BlobId id, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                return manifestEntry.IsNative;
            }

            return false;
        }

        public bool HasNativeBlob<T>(BlobId id, bool updateAccessTime = true)
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                return manifestEntry.IsNative && manifestEntry.Type == typeof(T);
            }

            return false;
        }

        internal bool TryGetManagedBlob<T>(BlobId id, out T value, bool updateAccessTime = true)
            where T : class
        {
            if (!TryGetBlobAndMetadata(id, out var valueBase, out var metadata, updateAccessTime))
            {
                value = default;
                return false;
            }

            Assert.That(!metadata.IsNative);
            Assert.That(
                typeof(T).IsAssignableFrom(metadata.Type),
                "Expected blob assignable to type {}, but found type {}",
                typeof(T),
                metadata.Type
            );

            value = (T)valueBase;
            return true;
        }

        internal T GetManagedBlob<T>(BlobId id, bool updateAccessTime = true)
            where T : class
        {
            Assert.That(!_hasDisposed);

            var blob = GetBlobAndMetadata(id, out var metadata, updateAccessTime);

            Assert.That(!metadata.IsNative);
            Assert.That(typeof(T).IsAssignableFrom(metadata.Type));

            return (T)blob;
        }

        public BlobPtr<T> CreateBlobPtr<T>(T blob)
            where T : class
        {
            Assert.That(!_hasDisposed);
            var id = CreateUniqueBlobId();
            RequireWritableStore().CreateBlobImpl(id, blob, isNative: false);
            return new BlobPtr<T>(CreateHandle(id), id);
        }

        public BlobPtr<T> CreateBlobPtr<T>(BlobId id, T blob)
            where T : class
        {
            Assert.That(!_hasDisposed);
            RequireWritableStore().CreateBlobImpl(id, blob, isNative: false);
            return new BlobPtr<T>(CreateHandle(id), id);
        }

        public bool TryCreateBlobPtr<T>(
            BlobId blobId,
            out BlobPtr<T> ptr,
            bool updateAccessTime = true
        )
            where T : class
        {
            if (!HasManagedBlob<T>(blobId, updateAccessTime: updateAccessTime))
            {
                ptr = default;
                return false;
            }

            ptr = new BlobPtr<T>(CreateHandle(blobId), blobId);
            return true;
        }

        public bool TryCreateNativeBlobPtr<T>(BlobId blobId, out NativeBlobPtr<T> ptr)
            where T : unmanaged
        {
            if (!HasNativeBlob<T>(blobId))
            {
                ptr = default;
                return false;
            }

            ptr = new NativeBlobPtr<T>(CreateHandle(blobId), blobId);
            return true;
        }

        public BlobPtr<T> CreateBlobPtr<T>(BlobId blobId)
            where T : class
        {
            if (!TryCreateBlobPtr<T>(blobId, out var ptr))
            {
                throw Assert.CreateException(
                    "Attempted to get ptr for unrecognized managed blob {}",
                    blobId
                );
            }
            return ptr;
        }

        public IBlobAnchor CreateBlobAnchor(BlobId blobId, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            if (
                !TryGetManifestEntry(
                    blobId,
                    out var manifestEntry,
                    updateAccessTime: updateAccessTime
                )
            )
            {
                throw Assert.CreateException(
                    "Attempted to get blob anchor for unrecognized native blob {}",
                    blobId
                );
            }

            var handle = CreateHandle(blobId);
            return new BlobAnchor(handle);
        }

        public NativeBlobPtr<T> CreateNativeBlobPtr<T>(BlobId blobId)
            where T : unmanaged
        {
            if (!TryCreateNativeBlobPtr<T>(blobId, out var ptr))
            {
                throw Assert.CreateException(
                    "Attempted to get ptr for unrecognized native blob {}",
                    blobId
                );
            }
            return ptr;
        }

        internal void ForcePurgeBlob(BlobId id)
        {
            _log.Warning(
                "Received call to BlobCache.ForcePurgeBlob - this method should not be used"
            );
            RequireWritableStore().ForcePurgeBlob(id);
        }

        internal unsafe bool TryGetNativeBlobPtr<T>(
            BlobId id,
            out IntPtr ptr,
            bool updateAccessTime = true
        )
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);

            if (!TryGetBlobAndMetadata(id, out var blob, out var metadata, updateAccessTime))
            {
                ptr = IntPtr.Zero;
                return false;
            }

            Assert.That(metadata.IsNative);
            Assert.That(
                metadata.Type == typeof(T),
                "Type mismatch retrieving native blob: stored {}, requested {}",
                metadata.Type,
                typeof(T)
            );

            ptr = ((NativeBlobBox)blob).Ptr;
            return true;
        }

        internal IntPtr GetNativeBlobPtr(BlobId id, int innerTypeId, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            _log.Trace("Looking up native blob with inner type id {}", innerTypeId);

            var blob = GetBlobAndMetadata(id, out var metadata, updateAccessTime);

            Assert.That(TypeIdProvider.GetTypeId(metadata.Type) == innerTypeId);
            Assert.That(metadata.IsNative);

            return ((NativeBlobBox)blob).Ptr;
        }

        internal unsafe ref T GetNativeBlobRef<T>(BlobId id, bool updateAccessTime = true)
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);

            var blob = GetBlobAndMetadata(id, out var metadata, updateAccessTime);

            Assert.That(metadata.Type == typeof(T));
            Assert.That(metadata.IsNative);

            return ref UnsafeUtility.AsRef<T>(((NativeBlobBox)blob).Ptr.ToPointer());
        }

        internal object GetBlob(BlobId id, bool updateAccessTime = true)
        {
            return GetBlobAndMetadata(id, out _, updateAccessTime);
        }

        public BlobMetadata GetBlobMetadata(BlobId id, bool updateAccessTime = true)
        {
            GetBlobAndMetadata(id, out var metadata, updateAccessTime);
            return metadata;
        }

        internal object GetBlobAndMetadata(
            BlobId id,
            out BlobMetadata metadata,
            bool updateAccessTime = true
        )
        {
            var result = TryGetBlobAndMetadata(id, out var blob, out metadata, updateAccessTime);
            Assert.That(result, "Attempted to get unrecognized blob id {}", id);
            return blob;
        }

        internal bool TryGetBlob(BlobId id, out object blob, bool updateAccessTime = true)
        {
            Assert.That(!_hasDisposed);

            return TryGetBlobAndMetadata(id, out blob, out _, updateAccessTime);
        }

        public void WarmUpBlob(BlobId id)
        {
            Assert.That(!_hasDisposed);

            foreach (var store in _stores)
            {
                if (store.HasBlob(id))
                {
                    store.WarmUpBlob(id);
                    return;
                }
            }

            throw Assert.CreateException("No blob store found for id {}", id);
        }

        public BlobLoadingState GetBlobLoadingState(BlobId id)
        {
            Assert.That(!_hasDisposed);

            foreach (var store in _stores)
            {
                if (store.HasBlob(id))
                {
                    return store.GetBlobLoadingState(id);
                }
            }

            throw Assert.CreateException("No blob store found for id {}", id);
        }

        internal PtrHandle CreateHandle(BlobId blobId)
        {
            var id = new PtrHandle(_handleIdCounter);
            _handleIdCounter += 1;
            _handles.Add(id, blobId);
            return id;
        }

        internal void DisposeHandle(PtrHandle handleId)
        {
            _handles.RemoveMustExist(handleId);
        }

        internal bool ContainsHandle(PtrHandle handleId)
        {
            return _handles.ContainsKey(handleId);
        }

        internal bool TryGetBlobAndMetadata(
            BlobId id,
            out object blob,
            out BlobMetadata metadata,
            bool updateAccessTime = true
        )
        {
            Assert.That(!_hasDisposed);

            using var _ = TrecsProfiling.Start("BlobCache.TryGetBlobAndMetadata");

            _log.Trace("Attempting to look up blob with id {}", id);

            foreach (var store in _stores)
            {
                if (
                    store.TryGetBlobAndMetadata(
                        id,
                        out blob,
                        out metadata,
                        updateAccessTime: updateAccessTime
                    )
                )
                {
                    return true;
                }
            }

            blob = null;
            metadata = default;
            return false;
        }

        BlobId CreateUniqueBlobId()
        {
            var value = _rng.NextLong();
            if (value == 0)
                value = 1; // Avoid colliding with BlobId.Null
            var blobId = new BlobId(value);
            _log.Trace("Created new blob id {}", blobId);
            return blobId;
        }

        public NativeBlobPtr<T> CreateNativeBlobPtr<T>(in T value)
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);
            return CreateNativeBlobPtr<T>(CreateUniqueBlobId(), in value);
        }

        public NativeBlobPtr<T> CreateNativeBlobPtr<T>(BlobId id, in T value)
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);
            var box = NativeBlobBox.AllocFromValue(in value);
            try
            {
                RequireWritableStore().CreateBlobImpl(id, box, isNative: true);
            }
            catch
            {
                box.Dispose();
                throw;
            }
            _log.Trace("Added new blob with id {} and type {}", id, typeof(T));
            return new NativeBlobPtr<T>(CreateHandle(id), id);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer and registers it as a blob.
        /// The caller must provide the exact allocation size and alignment so the blob
        /// can be freed correctly. This is critical for variable-sized types where the
        /// allocation is larger than sizeof(T).
        /// See <see cref="NativeUniqueHeap.AllocTakingOwnership{T}"/> for the ownership contract.
        /// </summary>
        public NativeBlobPtr<T> CreateNativeBlobPtrTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);
            return CreateNativeBlobPtrTakingOwnership<T>(
                CreateUniqueBlobId(),
                ptr,
                allocSize,
                allocAlignment
            );
        }

        public NativeBlobPtr<T> CreateNativeBlobPtrTakingOwnership<T>(
            BlobId id,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_hasDisposed);
            var box = NativeBlobBox.FromExistingPointer(ptr, allocSize, allocAlignment, typeof(T));
            try
            {
                RequireWritableStore().CreateBlobImpl(id, box, isNative: true);
            }
            catch
            {
                box.Dispose();
                throw;
            }
            _log.Trace("Added new blob (ownership transfer) with id {} and type {}", id, typeof(T));
            return new NativeBlobPtr<T>(CreateHandle(id), id);
        }

        public void Dispose()
        {
            if (_hasDisposed)
            {
                return;
            }

            if (_handles.Count > 0)
            {
                var seenTypes = new HashSet<Type>();
                var typeNames = new System.Text.StringBuilder();

                foreach (var (_, blobId) in _handles)
                {
                    if (
                        TryGetManifestEntry(blobId, out var manifestEntry, updateAccessTime: false)
                        && seenTypes.Add(manifestEntry.Type)
                    )
                    {
                        if (typeNames.Length > 0)
                        {
                            typeNames.Append(", ");
                        }

                        typeNames.Append(manifestEntry.Type.GetPrettyName());
                    }
                }

                _log.Warning(
                    "Found {} undisposed blob handles (types: {})",
                    _handles.Count,
                    typeNames
                );
            }
            else
            {
                _log.Debug("All blob handles properly disposed");
            }

            foreach (var store in _stores)
            {
                store.Dispose();
            }

            _hasDisposed = true;
        }

        public static readonly ReadOnlyDenseHashSet<int> SerializationFlags =
            new DenseHashSet<int>();
    }
}
