using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    public sealed class BlobStoreCommon
    {
        TrecsLog _log;

        readonly List<NativeCleanupEntry> _nativeCleanBuffer = new();
        readonly List<ManagedCleanupEntry> _managedCleanBuffer = new();
        readonly ITrecsPoolManager _poolManager;

        // Monotonic, in-process LRU counter. We use a counter rather than
        // DateTime.Now.Ticks so ordering is robust against wall-clock changes
        // (NTP, DST) and so back-to-back accesses never tie. Persisted manifests
        // (e.g. BlobStoreFiles) call BumpCounterAbove after load to keep
        // cross-run LRU ordering coherent — see remarks on BlobMetadata.
        long _accessCounter = 1;

        public BlobStoreCommon(ITrecsPoolManager poolManager)
        {
            _poolManager = poolManager;
        }

        public TrecsLog Log
        {
            set { _log = value; }
        }

        public long NextAccessTime()
        {
            var result = _accessCounter;
            _accessCounter += 1;
            return result;
        }

        /// <summary>
        /// Ensures the next <see cref="NextAccessTime"/> result is strictly greater than
        /// <paramref name="minValue"/>. Call after loading a persisted manifest so newly-accessed
        /// blobs sort as more recent than anything restored from disk.
        /// </summary>
        public void BumpCounterAbove(long minValue)
        {
            if (minValue >= _accessCounter)
            {
                _accessCounter = minValue + 1;
            }
        }

        /// <summary>
        /// Walks <paramref name="cache"/> and adds the contribution of every inactive
        /// entry (not present in <paramref name="activeBlobs"/>) to the running totals.
        /// Used by <see cref="IBlobStore.SumInMemoryInactiveTotals"/> implementations
        /// to feed <see cref="BlobCache"/>'s post-clean recount. Cheaper than the full
        /// LRU-build inside <see cref="CleanMemoryCache"/> because no per-entry buffer
        /// is materialized.
        /// </summary>
        public void SumInMemoryInactiveTotals(
            ReadOnlyBlobIdSet activeBlobs,
            IterableDictionary<BlobId, object> cache,
            BlobManifest manifest,
            ref long nativeBytes,
            ref int managedCount
        )
        {
            foreach (var (blobId, _) in cache)
            {
                if (activeBlobs.Contains(blobId))
                {
                    continue;
                }

                var manifestEntry = manifest.Values[blobId];

                if (manifestEntry.IsNative)
                {
                    nativeBytes += manifestEntry.NativeBytes;
                }
                else
                {
                    managedCount += 1;
                }
            }
        }

        /// <summary>
        /// Walks <paramref name="cache"/> once and produces a full
        /// <see cref="BlobStoreStats"/>: total + inactive native bytes and
        /// total + inactive managed-entry counts. Used by
        /// <see cref="IBlobStore.GetStats"/> implementations; pairs the
        /// hot-path-only <see cref="SumInMemoryInactiveTotals"/> with a
        /// broader observability view that also reports active entries.
        /// </summary>
        public BlobStoreStats GetStats(
            ReadOnlyBlobIdSet activeBlobs,
            IterableDictionary<BlobId, object> cache,
            BlobManifest manifest
        )
        {
            long totalNativeBytes = 0;
            long inactiveNativeBytes = 0;
            int totalManagedEntries = 0;
            int inactiveManagedEntries = 0;

            foreach (var (blobId, _) in cache)
            {
                var manifestEntry = manifest.Values[blobId];
                bool isInactive = !activeBlobs.Contains(blobId);

                if (manifestEntry.IsNative)
                {
                    totalNativeBytes += manifestEntry.NativeBytes;

                    if (isInactive)
                    {
                        inactiveNativeBytes += manifestEntry.NativeBytes;
                    }
                }
                else
                {
                    totalManagedEntries += 1;

                    if (isInactive)
                    {
                        inactiveManagedEntries += 1;
                    }
                }
            }

            return new BlobStoreStats(
                totalNativeMemoryBytes: totalNativeBytes,
                inactiveNativeMemoryBytes: inactiveNativeBytes,
                totalManagedEntries: totalManagedEntries,
                inactiveManagedEntries: inactiveManagedEntries
            );
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

        /// <summary>
        /// Evicts inactive (unpinned) blobs from <paramref name="cache"/> to bring the cache
        /// back under its limits. Native and managed blobs are governed separately:
        /// native blobs by total bytes, managed blobs by entry count (managed object size
        /// is not knowable in C#). Both passes are LRU-ordered using the per-blob
        /// <see cref="BlobMetadata.LastAccessTime"/>; active blobs are never evicted.
        /// </summary>
        public void CleanMemoryCache(
            ReadOnlyBlobIdSet activeBlobs,
            IterableDictionary<BlobId, object> cache,
            float maxInactiveNativeBlobsMb,
            int maxInactiveManagedBlobsCount,
            BlobManifest manifest
        )
        {
            var nativeQueue = _nativeCleanBuffer;
            var managedQueue = _managedCleanBuffer;
            nativeQueue.Clear();
            managedQueue.Clear();

            long inactiveNativeBytes = 0;
            long totalNativeBytes = 0;
            int totalManagedCount = 0;

            foreach (var (blobId, _) in cache)
            {
                var manifestEntry = manifest.Values[blobId];
                bool isActive = activeBlobs.Contains(blobId);

                if (manifestEntry.IsNative)
                {
                    totalNativeBytes += manifestEntry.NativeBytes;

                    if (!isActive)
                    {
                        inactiveNativeBytes += manifestEntry.NativeBytes;
                        nativeQueue.Add(
                            new()
                            {
                                BlobId = blobId,
                                NativeBytes = manifestEntry.NativeBytes,
                                LastAccessTime = manifestEntry.LastAccessTime,
                            }
                        );
                    }
                }
                else
                {
                    totalManagedCount += 1;

                    if (!isActive)
                    {
                        managedQueue.Add(
                            new() { BlobId = blobId, LastAccessTime = manifestEntry.LastAccessTime }
                        );
                    }
                }
            }

            _log?.Trace(
                "Blob native memory usage: {0:0.00}mb total, {1:0.00}mb inactive",
                (float)totalNativeBytes / 1024f / 1024f,
                (float)inactiveNativeBytes / 1024f / 1024f
            );
            _log?.Trace(
                "Blob managed entry count: {0} total, {1} inactive",
                totalManagedCount,
                managedQueue.Count
            );

            EvictNativeBlobs(cache, nativeQueue, inactiveNativeBytes, maxInactiveNativeBlobsMb);
            EvictManagedBlobs(cache, managedQueue, maxInactiveManagedBlobsCount);
        }

        void EvictNativeBlobs(
            IterableDictionary<BlobId, object> cache,
            List<NativeCleanupEntry> queue,
            long inactiveNativeBytes,
            float maxInactiveNativeBlobsMb
        )
        {
            long maxInactiveBytes = (long)(maxInactiveNativeBlobsMb * 1024f * 1024f);
            long bytesToRemove = inactiveNativeBytes - maxInactiveBytes;

            if (bytesToRemove <= 0)
            {
                return;
            }

            queue.Sort(_byNativeLastAccessTime);

            var numRemoved = 0;

            foreach (var info in queue)
            {
                bytesToRemove -= info.NativeBytes;

                var wasRemoved = cache.TryRemove(info.BlobId, out var blobValue);
                TrecsDebugAssert.That(wasRemoved);

                _log?.Trace("Disposing native blob {0}", info.BlobId);
                DisposeBlob(blobValue, isNative: true);

                numRemoved += 1;

                if (bytesToRemove <= 0)
                {
                    break;
                }
            }

            _log?.Debug(
                "Removed {0} native blobs from in-memory cache to get under byte limit",
                numRemoved
            );
        }

        void EvictManagedBlobs(
            IterableDictionary<BlobId, object> cache,
            List<ManagedCleanupEntry> queue,
            int maxInactiveManagedBlobsCount
        )
        {
            int countToRemove = queue.Count - maxInactiveManagedBlobsCount;

            if (countToRemove <= 0)
            {
                return;
            }

            queue.Sort(_byManagedLastAccessTime);

            var numRemoved = 0;

            foreach (var info in queue)
            {
                var wasRemoved = cache.TryRemove(info.BlobId, out var blobValue);
                TrecsDebugAssert.That(wasRemoved);

                _log?.Trace("Disposing managed blob {0}", info.BlobId);
                DisposeBlob(blobValue, isNative: false);

                numRemoved += 1;

                if (numRemoved >= countToRemove)
                {
                    break;
                }
            }

            _log?.Debug(
                "Removed {0} managed blobs from in-memory cache to get under count limit",
                numRemoved
            );
        }

        public const int ManifestSerializationVersion = 1;

        // Cached so .Sort doesn't allocate a Comparison delegate per cleanup tick.
        static readonly Comparison<NativeCleanupEntry> _byNativeLastAccessTime = (a, b) =>
            a.LastAccessTime.CompareTo(b.LastAccessTime);

        static readonly Comparison<ManagedCleanupEntry> _byManagedLastAccessTime = (a, b) =>
            a.LastAccessTime.CompareTo(b.LastAccessTime);

        struct NativeCleanupEntry
        {
            public BlobId BlobId;
            public long NativeBytes;
            public long LastAccessTime;
        }

        struct ManagedCleanupEntry
        {
            public BlobId BlobId;
            public long LastAccessTime;
        }
    }
}
