using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Configuration for <see cref="BlobStoreInMemory"/>.
    /// </summary>
    /// <remarks>
    /// Limits apply only to <i>inactive</i> blobs (those with no live <see cref="SharedPtr{T}"/>
    /// or other pinning handle). Active blobs are always retained — eviction can never
    /// pull bytes out from under a live pointer.
    /// </remarks>
    public sealed class BlobStoreInMemorySettings
    {
        /// <summary>
        /// Maximum megabytes of inactive native blobs to retain in the cache. The byte cost of a
        /// managed (class) blob is not knowable in C#, so managed blobs are governed separately
        /// by <see cref="MaxInactiveManagedBlobsCount"/>.
        /// </summary>
        public float MaxInactiveNativeBlobsMb { get; init; } = 100f;

        /// <summary>
        /// Maximum number of inactive managed (class) blobs to retain in the cache. When the
        /// inactive-managed-blob count exceeds this, the least-recently-used blobs are evicted
        /// first.
        /// </summary>
        public int MaxInactiveManagedBlobsCount { get; init; } = 1024;

        public static readonly BlobStoreInMemorySettings Default = new();
    }

    /// <summary>
    /// In-memory <see cref="IBlobStore"/> implementation that holds all blobs in a dictionary.
    /// Supports LRU eviction when the memory limit is exceeded.
    /// </summary>
    public sealed class BlobStoreInMemory : IBlobStore
    {
        TrecsLog _log;

        readonly IterableDictionary<BlobId, object> _memoryCache = new();
        readonly BlobManifest _manifest = new();
        readonly BlobStoreCommon _common;
        readonly BlobStoreInMemorySettings _settings;

        bool _hasDisposed;

        public BlobStoreInMemory(
            BlobStoreInMemorySettings settings,
            ITrecsPoolManager poolManager = null
        )
        {
            TrecsAssert.That(settings != null, "settings must not be null");
            TrecsAssert.That(
                settings.MaxInactiveNativeBlobsMb >= 0,
                "MaxInactiveNativeBlobsMb must be non-negative, was {0}",
                settings.MaxInactiveNativeBlobsMb
            );
            TrecsAssert.That(
                settings.MaxInactiveManagedBlobsCount >= 0,
                "MaxInactiveManagedBlobsCount must be non-negative, was {0}",
                settings.MaxInactiveManagedBlobsCount
            );
            _settings = settings;
            _common = new(poolManager);
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public ReadOnlyIterableDictionary<BlobId, object> MemoryCache
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
            TrecsDebugAssert.That(!_hasDisposed);

            TrecsDebugAssert.That(_manifest.Values.ContainsKey(id));

            if (_memoryCache.TryRemove(id, out var blob))
            {
                _common.DisposeBlob(blob, _manifest.Values[id].IsNative);
            }

            if (!_manifest.Values.TryRemove(id, out var _))
            {
                _log?.Warning("Blob with id {0} was not found in manifest when force purging", id);
            }

            _log?.Debug("Force removed blob with id {0}", id);
        }

        public void CreateBlobImpl(BlobId id, object blob, bool isNative)
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.That(
                !_memoryCache.ContainsKey(id) && !_manifest.Values.ContainsKey(id)
            );

            _memoryCache.Add(id, blob);

            Type metadataType;
            long nativeBytes;

            if (isNative)
            {
                var box = (NativeBlobBox)blob;
                metadataType = box.InnerType;
                nativeBytes = box.Size;
            }
            else
            {
                metadataType = blob.GetType();
                TrecsDebugAssert.That(metadataType.IsClass);
                nativeBytes = 0;
            }

            _manifest.Values.Add(
                id,
                new BlobMetadata
                {
                    LastAccessTime = _common.NextAccessTime(),
                    NativeBytes = nativeBytes,
                    IsNative = isNative,
                    TypeId = TypeId.FromType(metadataType),
                }
            );
        }

        public void CleanCache(ReadOnlyBlobIdSet activeBlobs)
        {
            _common.CleanMemoryCache(
                activeBlobs,
                _memoryCache,
                maxInactiveNativeBlobsMb: _settings.MaxInactiveNativeBlobsMb,
                maxInactiveManagedBlobsCount: _settings.MaxInactiveManagedBlobsCount,
                _manifest
            );
        }

        public long MaxInactiveNativeBytes
        {
            get { return (long)(_settings.MaxInactiveNativeBlobsMb * 1024f * 1024f); }
        }

        public int MaxInactiveManagedCount
        {
            get { return _settings.MaxInactiveManagedBlobsCount; }
        }

        public void SumInMemoryInactiveTotals(
            ReadOnlyBlobIdSet activeBlobs,
            ref long nativeBytes,
            ref int managedCount
        )
        {
            TrecsDebugAssert.That(!_hasDisposed);
            _common.SumInMemoryInactiveTotals(
                activeBlobs,
                _memoryCache,
                _manifest,
                ref nativeBytes,
                ref managedCount
            );
        }

        public BlobStoreStats GetStats(ReadOnlyBlobIdSet activeBlobs)
        {
            TrecsDebugAssert.That(!_hasDisposed);
            return _common.GetStats(activeBlobs, _memoryCache, _manifest);
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            _hasDisposed = true;

            int numDisposed = 0;

            foreach (var (blobId, blob) in _memoryCache)
            {
                _common.DisposeBlob(blob, _manifest.Values[blobId].IsNative);
                numDisposed += 1;
            }

            _log?.Debug("Disposed {0} blobs", numDisposed);

            _memoryCache.Clear();
        }

        public bool TryGetManifestEntry(
            BlobId id,
            out BlobMetadata manifestEntry,
            bool updateAccessTime
        )
        {
            TrecsDebugAssert.That(!_hasDisposed);

            if (_manifest.Values.TryGetIndex(id, out var index))
            {
                ref var entry = ref _manifest.Values.GetValueAtIndexByRef(index);

                if (updateAccessTime)
                {
                    entry.LastAccessTime = _common.NextAccessTime();
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
            TrecsDebugAssert.That(!_hasDisposed);

            _log?.Trace("Attempting to look up blob with id {0}", id);

            if (!_manifest.Values.TryGetIndex(id, out var index))
            {
                _log?.Trace("No cached file found for blob {0}", id);
                blob = null;
                metadata = default;
                return false;
            }

            ref var entry = ref _manifest.Values.GetValueAtIndexByRef(index);

            blob = _memoryCache[id];
            TrecsDebugAssert.IsNotNull(blob);

            _log?.Trace(
                "Found blob {0} of type {1} already in memory cache",
                id,
                TypeId.ToType(entry.TypeId)
            );

            if (updateAccessTime)
            {
                entry.LastAccessTime = _common.NextAccessTime();
            }

            metadata = entry;
            return true;
        }

        public bool Contains(BlobId id)
        {
            TrecsDebugAssert.That(!_hasDisposed);
            return _manifest.Values.ContainsKey(id);
        }

        public void WarmUpBlob(BlobId id)
        {
            TrecsDebugAssert.That(_manifest.Values.ContainsKey(id));
        }

        public BlobLoadingState GetBlobLoadingState(BlobId id)
        {
            TrecsDebugAssert.That(_manifest.Values.ContainsKey(id));
            return BlobLoadingState.Loaded;
        }
    }
}
