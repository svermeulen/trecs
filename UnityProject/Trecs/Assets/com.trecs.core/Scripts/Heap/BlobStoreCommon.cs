using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    public sealed class BlobStoreCommon
    {
        TrecsLog _log;

        readonly List<MemoryCacheCleanupToRemoveInfo> _cleanBuffer2 = new();
        readonly ITrecsPoolManager _poolManager;

        public BlobStoreCommon(ITrecsPoolManager poolManager)
        {
            _poolManager = poolManager;
        }

        public TrecsLog Log
        {
            set { _log = value; }
        }

        public void DisposeBlob(object blob, bool isNative)
        {
            if (isNative)
            {
                ((IDisposable)blob).Dispose();
            }
            else
            {
                _poolManager?.Despawn(blob.GetType(), blob);
            }
        }

        public void CleanMemoryCache(
            DenseHashSet<BlobId> activeBlobs,
            DenseDictionary<BlobId, object> cache,
            float maxMemoryCacheMb,
            BlobManifest manifest
        )
        {
            var removeQueue = _cleanBuffer2;
            removeQueue.Clear();

            long numExtraBytes = 0;
            long totalNumBytes = 0;

            foreach (var (blobId, _) in cache)
            {
                var manifestEntry = manifest.Values[blobId];
                totalNumBytes += manifestEntry.NumBytes;

                if (!activeBlobs.Contains(blobId))
                {
                    _log?.Trace(
                        "Could not find blob {0} in active blob list - adding to remove queue",
                        blobId
                    );

                    numExtraBytes += manifestEntry.NumBytes;
                    removeQueue.Add(
                        new()
                        {
                            BlobId = blobId,
                            NumBytes = manifestEntry.NumBytes,
                            IsNative = manifestEntry.IsNative,
                            LastAccessTime = manifestEntry.LastAccessTime,
                        }
                    );
                }
            }

            // _dbg.Text("Blob Total Memory Usage: {0.00} mb", (float)totalNumBytes / 1024f / 1024f);
            _log?.Trace(
                "Blob Total Memory Usage: {0:0.00} mb",
                (float)totalNumBytes / 1024f / 1024f
            );

            // _dbg.Text(
            //     "Blob Extra Memory Usage: {0.00} mb / {0.00} mb",
            //     (float)numExtraBytes / 1024f / 1024f,
            //     maxMemoryCacheMb
            // );
            _log?.Trace(
                "Blob Extra Memory Usage: {0:0.00} mb / {1:0.00} mb",
                (float)numExtraBytes / 1024f / 1024f,
                maxMemoryCacheMb
            );

            long maxMemoryCacheNumBytes = (long)(maxMemoryCacheMb * 1024f * 1024f);
            var bytesToRemove = numExtraBytes - maxMemoryCacheNumBytes;

            if (bytesToRemove <= 0)
            {
                // _log?.Trace("No need to clean memory cache - under limit");
                return;
            }

            var numRemoved = 0;

            removeQueue.Sort(_byLastAccessTime);

            foreach (var info in removeQueue)
            {
                bytesToRemove -= info.NumBytes;

                var wasRemoved = cache.TryRemove(info.BlobId, out var blobValue);
                TrecsAssert.That(wasRemoved);

                _log?.Trace("Disposing blob {0}", info.BlobId);
                DisposeBlob(blobValue, info.IsNative);

                numRemoved += 1;

                if (bytesToRemove <= 0)
                {
                    break;
                }
            }

            _log?.Debug(
                "Removed {0} blobs from in-memory cache, to get under memory limit",
                numRemoved
            );
        }

        public const int ManifestSerializationVersion = 1;

        // Cached so .Sort doesn't allocate a Comparison delegate per cleanup tick.
        static readonly Comparison<MemoryCacheCleanupToRemoveInfo> _byLastAccessTime = (a, b) =>
            a.LastAccessTime.CompareTo(b.LastAccessTime);

        struct MemoryCacheCleanupToRemoveInfo
        {
            public BlobId BlobId;
            public long NumBytes;
            public bool IsNative;
            public long LastAccessTime;
        }
    }
}
