using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Configuration for <see cref="BlobStoreInMemory"/>.
    /// </summary>
    public sealed class BlobStoreInMemorySettings
    {
        /// <summary>
        /// Maximum memory (MB) before the LRU cache begins evicting unused blobs.
        /// </summary>
        public float MaxMemoryCacheMb;
    }

    /// <summary>
    /// In-memory <see cref="IBlobStore"/> implementation that holds all blobs in a dictionary.
    /// Supports LRU eviction when the memory limit is exceeded.
    /// </summary>
    public sealed class BlobStoreInMemory : IBlobStore
    {
        TrecsLog _log;

        readonly DenseDictionary<BlobId, object> _memoryCache = new();
        readonly BlobManifest _manifest = new();
        readonly BlobStoreCommon _common;
        readonly BlobStoreInMemorySettings _settings;

        bool _hasDisposed;

        public BlobStoreInMemory(
            BlobStoreInMemorySettings settings,
            ITrecsPoolManager poolManager = null
        )
        {
            _settings = settings;
            _common = new(poolManager);
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public ReadOnlyDenseDictionary<BlobId, object> MemoryCache
        {
            get { return _memoryCache; }
        }

        public int SerializationVersion
        {
            // In-memory store doesn't serialize anything, so the version
            // wired up by BlobCache is intentionally ignored here.
            set { }
        }

        public NativeBlobBoxPool NativeBlobBoxPool
        {
            // In-memory store never constructs new boxes — it just stores the
            // box objects handed to it by BlobCache — so the pool is unused.
            set { }
        }

        public TrecsLog Log
        {
            set
            {
                _log = value;
                _common.Log = value;
            }
        }

        public void ForcePurgeBlob(BlobId id)
        {
            Assert.That(!_hasDisposed);

            Assert.That(_manifest.Values.ContainsKey(id));

            if (_memoryCache.TryRemove(id, out var blob))
            {
                _common.DisposeBlob(blob, _manifest.Values[id].IsNative);
            }

            if (!_manifest.Values.TryRemove(id, out var _))
            {
                _log.Warning("Blob with id {} was not found in manifest when force purging", id);
            }

            _log.Debug("Force removed blob with id {}", id);
        }

        public void CreateBlobImpl(BlobId id, object blob, bool isNative)
        {
            Assert.That(!_hasDisposed);
            Assert.That(!_memoryCache.ContainsKey(id) && !_manifest.Values.ContainsKey(id));

            _memoryCache.Add(id, blob);

            Type metadataType;
            long numBytes;

            if (isNative)
            {
                var box = (NativeBlobBox)blob;
                metadataType = box.InnerType;
                numBytes = box.Size;
            }
            else
            {
                metadataType = blob.GetType();
                Assert.That(metadataType.IsClass);
                numBytes = 0;
            }

            _manifest.Values.Add(
                id,
                new BlobMetadata
                {
                    LastAccessTime = BlobManifest.GetTimeForAccessTime(),
                    NumBytes = numBytes,
                    IsNative = isNative,
                    Type = metadataType,
                }
            );
        }

        public void CleanCache(DenseHashSet<BlobId> activeBlobs)
        {
            _common.CleanMemoryCache(
                activeBlobs,
                _memoryCache,
                maxMemoryCacheMb: _settings.MaxMemoryCacheMb,
                _manifest
            );
        }

        public void Dispose()
        {
            Assert.That(!_hasDisposed);
            _hasDisposed = true;

            int numDisposed = 0;

            foreach (var (blobId, blob) in _memoryCache)
            {
                _common.DisposeBlob(blob, _manifest.Values[blobId].IsNative);
                numDisposed += 1;
            }

            _log.Debug("Disposed {} blobs", numDisposed);

            _memoryCache.Clear();
        }

        public bool TryGetManifestEntry(
            BlobId id,
            out BlobMetadata manifestEntry,
            bool updateAccessTime
        )
        {
            Assert.That(!_hasDisposed);

            if (_manifest.Values.TryGetIndex(id, out var index))
            {
                ref var entry = ref _manifest.Values.GetValueAtIndexByRef(index);

                if (updateAccessTime)
                {
                    entry.LastAccessTime = BlobManifest.GetTimeForAccessTime();
                }

                manifestEntry = entry;
                return true;
            }

            manifestEntry = default;
            return false;
        }

        public bool TryGetBlobAndMetadata(
            BlobId id,
            out object blob,
            out BlobMetadata metadata,
            bool updateAccessTime
        )
        {
            Assert.That(!_hasDisposed);

            _log.Trace("Attempting to look up blob with id {}", id);

            if (!_manifest.Values.TryGetIndex(id, out var index))
            {
                _log.Trace("No cached file found for blob {}", id);
                blob = null;
                metadata = default;
                return false;
            }

            ref var entry = ref _manifest.Values.GetValueAtIndexByRef(index);

            blob = _memoryCache[id];
            Assert.IsNotNull(blob);

            _log.Trace("Found blob {} of type {} already in memory cache", id, entry.Type);
            entry.LastAccessTime = BlobManifest.GetTimeForAccessTime();
            metadata = entry;
            return true;
        }

        public bool HasBlob(BlobId id)
        {
            Assert.That(!_hasDisposed);
            return _manifest.Values.ContainsKey(id);
        }

        public void WarmUpBlob(BlobId id)
        {
            Assert.That(_manifest.Values.ContainsKey(id));
        }

        public BlobLoadingState GetBlobLoadingState(BlobId id)
        {
            Assert.That(_manifest.Values.ContainsKey(id));
            return BlobLoadingState.Loaded;
        }
    }
}
