using System;
using System.Collections.Generic;
using System.Text;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Configuration for the <see cref="BlobCache"/> cleanup trigger and serialization version.
    /// </summary>
    /// <remarks>
    /// Eviction is driven inline by every allocation and handle-dispose: when the
    /// running estimate of inactive bytes / count in any store exceeds the
    /// store's configured cap multiplied by <see cref="HighWaterMarkMultiplier"/>,
    /// a clean pass runs immediately and drains back down to the configured cap.
    /// There is no periodic tick — see <see cref="BlobCache"/>.
    /// </remarks>
    public sealed class BlobCacheSettings
    {
        /// <summary>
        /// Multiplier applied to each store's <c>MaxInactiveNativeBlobsMb</c> /
        /// <c>MaxInactiveManagedBlobsCount</c> to derive the high-water mark that
        /// triggers an inline eviction pass. Values closer to <c>1.0</c> keep memory
        /// tighter to the cap at the cost of more frequent eviction passes; higher
        /// values amortize the eviction cost over more allocations at the cost of
        /// larger transient overshoot. Must be &gt;= <c>1.0</c>.
        /// </summary>
        public float HighWaterMarkMultiplier { get; init; } = 1.5f;

        public int SerializationVersion { get; init; } = 1;

        public static readonly BlobCacheSettings Default = new();
    }

    /// <summary>
    /// Loading state of a blob in the <see cref="BlobCache"/>. Used with
    /// <see cref="IBlobPtr.GetLoadingState"/> for asynchronous blob warm-up.
    /// </summary>
    public enum BlobLoadingState
    {
        NotLoaded,
        Loading,
        Loaded,
    }

    /// <summary>
    /// Per-store snapshot of in-memory cache occupancy, returned by
    /// <see cref="IBlobStore.GetStats"/>. Useful for tuning each store's
    /// <c>MaxInactiveNativeBlobsMb</c> / <c>MaxInactiveManagedBlobsCount</c>
    /// caps and for diagnosing which store in a multi-store setup is filling up.
    /// <para>
    /// "Total" and "Inactive" cover only entries currently <i>resident in the
    /// store's in-memory cache</i>. Blobs that are known to the store's manifest
    /// but not currently loaded (e.g. a file-backed blob that has been evicted
    /// from memory but still has bytes on disk) do not contribute to either total.
    /// </para>
    /// </summary>
    public readonly struct BlobStoreStats
    {
        /// <summary>
        /// Total bytes of native (unmanaged) blobs currently resident in the
        /// store's in-memory cache, including both active (pinned by a handle)
        /// and inactive (evictable) entries.
        /// </summary>
        public readonly long TotalNativeMemoryBytes;

        /// <summary>
        /// Bytes of native blobs in the store's in-memory cache that are not
        /// currently pinned by any handle and could be evicted by the next
        /// clean pass. The slack between this and the store's configured
        /// <c>MaxInactiveNativeBlobsMb</c> indicates how much native cache
        /// budget remains before the high-water-mark trigger fires.
        /// </summary>
        public readonly long InactiveNativeMemoryBytes;

        /// <summary>
        /// Total number of managed (class) blobs currently resident in the
        /// store's in-memory cache, including both active and inactive entries.
        /// </summary>
        public readonly int TotalManagedEntries;

        /// <summary>
        /// Number of managed blobs in the store's in-memory cache that are
        /// not currently pinned by any handle. The slack between this and
        /// the store's configured <c>MaxInactiveManagedBlobsCount</c> indicates
        /// how much managed cache budget remains before eviction fires.
        /// </summary>
        public readonly int InactiveManagedEntries;

        public BlobStoreStats(
            long totalNativeMemoryBytes,
            long inactiveNativeMemoryBytes,
            int totalManagedEntries,
            int inactiveManagedEntries
        )
        {
            TotalNativeMemoryBytes = totalNativeMemoryBytes;
            InactiveNativeMemoryBytes = inactiveNativeMemoryBytes;
            TotalManagedEntries = totalManagedEntries;
            InactiveManagedEntries = inactiveManagedEntries;
        }
    }

    /// <summary>
    /// Aggregate snapshot of <see cref="BlobCache"/> occupancy across every
    /// registered <see cref="IBlobStore"/>. Returned by
    /// <see cref="BlobCache.GetStats"/>. Use this to tune the cache's configured
    /// caps (<c>MaxInactiveNativeBlobsMb</c> / <c>MaxInactiveManagedBlobsCount</c>)
    /// in production and to diagnose cache thrashing.
    /// <para>
    /// "Total" and "Inactive" cover only entries currently resident in some store's
    /// in-memory cache. See <see cref="BlobStoreStats"/> for the per-entry
    /// definitions; the only field that exists at the cache level (not the
    /// store level) is <see cref="ActiveHandleCount"/>, since handles are tracked
    /// in <see cref="BlobCache"/> itself, not per-store.
    /// </para>
    /// </summary>
    public readonly struct BlobCacheStats
    {
        /// <summary>
        /// Total bytes of native blobs currently resident in any store's
        /// in-memory cache, including both active and inactive entries.
        /// </summary>
        public readonly long TotalNativeMemoryBytes;

        /// <summary>
        /// Bytes of native blobs in some store's in-memory cache that are not
        /// currently pinned by any handle. This is the figure compared against
        /// the aggregate <c>MaxInactiveNativeBlobsMb</c> cap to decide whether
        /// an inline eviction pass needs to fire.
        /// </summary>
        public readonly long InactiveNativeMemoryBytes;

        /// <summary>
        /// Total number of managed (class) blobs currently resident in any
        /// store's in-memory cache, including both active and inactive entries.
        /// </summary>
        public readonly int TotalManagedEntries;

        /// <summary>
        /// Number of managed blobs in some store's in-memory cache that are
        /// not currently pinned by any handle. Compared against the aggregate
        /// <c>MaxInactiveManagedBlobsCount</c> cap on the eviction-trigger path.
        /// </summary>
        public readonly int InactiveManagedEntries;

        /// <summary>
        /// Number of outstanding pinning handles (<see cref="BlobPtr{T}"/>,
        /// <see cref="NativeBlobPtr{T}"/>, <see cref="IBlobAnchor"/>) currently
        /// alive. Each handle keeps its referenced blob in the active set until
        /// disposed; a chronically rising count over time is a leak signal.
        /// </summary>
        public readonly int ActiveHandleCount;

        public BlobCacheStats(
            long totalNativeMemoryBytes,
            long inactiveNativeMemoryBytes,
            int totalManagedEntries,
            int inactiveManagedEntries,
            int activeHandleCount
        )
        {
            TotalNativeMemoryBytes = totalNativeMemoryBytes;
            InactiveNativeMemoryBytes = inactiveNativeMemoryBytes;
            TotalManagedEntries = totalManagedEntries;
            InactiveManagedEntries = inactiveManagedEntries;
            ActiveHandleCount = activeHandleCount;
        }
    }

    /// <summary>
    /// Central cache that unifies one or more <see cref="IBlobStore"/> backends, providing
    /// handle-based access to managed and native blobs. Manages reference counting,
    /// inline cache cleanup, and asynchronous blob loading. Register blob stores via
    /// <see cref="WorldBuilder.AddBlobStore"/>.
    /// <para>
    /// Eviction is driven by the alloc / handle-dispose hot path: a running estimate of
    /// inactive bytes / count is updated incrementally, and when it crosses the
    /// high-water mark (see <see cref="BlobCacheSettings.HighWaterMarkMultiplier"/>)
    /// a clean pass runs immediately and drains back down to the configured cap.
    /// </para>
    /// </summary>
    public sealed class BlobCache
    {
        readonly TrecsLog _log;

        readonly IterableDictionary<PtrHandle, BlobId> _handles = new();
        readonly IterableDictionary<BlobId, int> _blobRefCounts = new();
        readonly BlobCacheSettings _settings;
        readonly List<IBlobStore> _stores;
        readonly IBlobStore _writableStore;
        readonly NativeBlobBoxPool _nativeBlobBoxPool;

        // High-water marks derived from each store's configured cap × multiplier.
        // Aggregated across all stores so a single compare suffices on the hot path.
        // When the running estimator below crosses either, an inline CleanCaches pass
        // runs and re-anchors the estimator to truth (see RecomputeInactiveTotals).
        readonly long _nativeBytesHighWaterMark;
        readonly int _managedCountHighWaterMark;

        // Running estimate of inactive native bytes / inactive managed blob count
        // across all stores' memory caches. Only grows between cleans (we add on
        // 1→0 ref-count transitions but never subtract on 0→1 transitions to avoid
        // racing with lazy-loaded blobs). Recounted exactly inside RecomputeInactiveTotals
        // after every CleanCaches pass so the over-estimate is bounded.
        long _inactiveNativeBytesEstimate;
        int _inactiveManagedCountEstimate;

        uint _handleIdCounter = 1;
        bool _hasDisposed;

        public BlobCache(
            TrecsLog log,
            List<IBlobStore> stores,
            BlobCacheSettings settings,
            NativeBlobBoxPool nativeBlobBoxPool
        )
        {
            TrecsDebugAssert.IsNotNull(nativeBlobBoxPool);
            _log = log;
            _stores = stores;
            _settings = settings ?? BlobCacheSettings.Default;
            _nativeBlobBoxPool = nativeBlobBoxPool;

            TrecsAssert.That(
                _settings.HighWaterMarkMultiplier >= 1f,
                "BlobCacheSettings.HighWaterMarkMultiplier must be >= 1.0, was {0}",
                _settings.HighWaterMarkMultiplier
            );

            long totalNativeBytes = 0;
            int totalManagedCount = 0;

            foreach (var store in stores)
            {
                store.Log = log;
                store.SerializationVersion = _settings.SerializationVersion;
                store.NativeBlobBoxPool = _nativeBlobBoxPool;

                if (!store.IsReadOnly)
                {
                    TrecsDebugAssert.IsNull(_writableStore);
                    _writableStore = store;
                }

                totalNativeBytes += store.MaxInactiveNativeBytes;
                totalManagedCount += store.MaxInactiveManagedCount;
            }

            // Aggregate across stores: any store hitting its own cap will be drained
            // by CleanCaches, but we only need a single compare to decide whether to
            // run that pass. Using the sum is a conservative trigger — at worst we
            // run CleanCaches a touch earlier than strictly necessary, which is fine
            // since per-store CleanCache is a no-op when under cap.
            // Compute in double so we don't lose precision for large caps (long-to-float
            // round-trips at ~7 significant digits), and clamp before narrowing.
            _nativeBytesHighWaterMark = (long)
                Math.Min(
                    (double)totalNativeBytes * _settings.HighWaterMarkMultiplier,
                    long.MaxValue
                );
            _managedCountHighWaterMark = (int)
                Math.Min(
                    (double)totalManagedCount * _settings.HighWaterMarkMultiplier,
                    int.MaxValue
                );
        }

        public IReadOnlyList<IBlobStore> BlobStores
        {
            get { return _stores; }
        }

        IBlobStore RequireWritableStore()
        {
            TrecsDebugAssert.IsNotNull(_writableStore, "No writable blob store found");
            return _writableStore;
        }

        // BlobCache is a main-thread-only abstraction. Asserts both invariants
        // (alive + on the right thread) at the top of every method so a
        // misuse from a job surfaces immediately instead of corrupting state.
        void AssertMainThreadAndNotDisposed()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread, "BlobCache is main-thread only");
        }

        bool TryGetManifestEntry(BlobId id, out BlobMetadata manifestEntry, bool updateAccessTime)
        {
            AssertMainThreadAndNotDisposed();

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
            AssertMainThreadAndNotDisposed();

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime))
            {
                TrecsDebugAssert.That(!manifestEntry.IsNative);
                return TypeId.ToType(manifestEntry.TypeId);
            }

            return null;
        }

        internal Type GetManagedBlobType(BlobId id, bool updateAccessTime = true)
        {
            var result = TryGetManagedBlobType(id, updateAccessTime);
            TrecsDebugAssert.IsNotNull(result);
            return result;
        }

        internal Type TryGetNativeBlobType(BlobId id, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime))
            {
                TrecsDebugAssert.That(manifestEntry.IsNative);
                return TypeId.ToType(manifestEntry.TypeId);
            }

            return null;
        }

        internal Type GetNativeBlobType(BlobId id, bool updateAccessTime = true)
        {
            var result = TryGetNativeBlobType(id, updateAccessTime);
            TrecsDebugAssert.IsNotNull(result);
            return result;
        }

        /// <summary>
        /// Copy every <see cref="BlobId"/> currently pinned by at least one outstanding
        /// handle into <paramref name="blobIds"/>. The output is the deduplicated set
        /// of active blobs — multiple handles for the same blob contribute one entry.
        /// O(active blobs); maintained incrementally on every handle create/dispose,
        /// not by walking the full handle table.
        /// </summary>
        public void GetAllActiveBlobIds(IterableHashSet<BlobId> blobIds)
        {
            AssertMainThreadAndNotDisposed();

            foreach (var blobId in _blobRefCounts.Keys)
            {
                blobIds.Add(blobId);
            }
        }

        /// <summary>
        /// Run an eviction pass across every registered <see cref="IBlobStore"/> and
        /// re-anchor the inactive-bytes / inactive-count estimators to truth. Called
        /// inline from the alloc / handle-dispose hot path when either estimator
        /// crosses the high-water mark derived from
        /// <see cref="BlobCacheSettings.HighWaterMarkMultiplier"/>.
        /// <para><b>Main-thread only</b> and must not be called while a job is
        /// reading from any store's memory cache — a concurrent eviction can free
        /// entries that jobs may be reading via <see cref="NativeSharedPtrResolver"/>.
        /// The hot-path trigger paths (<c>AllocXxxBlob</c>, handle-dispose) all
        /// already satisfy <see cref="AssertMainThreadAndNotDisposed"/>.</para>
        /// </summary>
        public void CleanCaches()
        {
            AssertMainThreadAndNotDisposed();

            // The ref-count dictionary's keyset is the active-blob set: a blob is
            // active iff at least one handle pins it, which is exactly when the
            // dictionary has an entry for it. Wrapping it directly avoids the
            // O(handles) walk a separate IterableHashSet rebuild would cost on every
            // eviction pass.
            var activeBlobs = new ReadOnlyBlobIdSet(_blobRefCounts);

            foreach (var store in _stores)
            {
                store.CleanCache(activeBlobs);
            }

            RecomputeInactiveTotals(activeBlobs);
        }

        // Re-anchor the running inactive estimators to truth by scanning each
        // store's manifest. Called after CleanCaches so the estimators absorb the
        // evicted bytes (and any drift from skipped 0→1 subtractions); also called
        // opportunistically if either estimator goes negative due to a transient
        // accounting mismatch.
        void RecomputeInactiveTotals(ReadOnlyBlobIdSet activeBlobs)
        {
            long nativeBytes = 0;
            int managedCount = 0;

            foreach (var store in _stores)
            {
                store.SumInMemoryInactiveTotals(activeBlobs, ref nativeBytes, ref managedCount);
            }

            _inactiveNativeBytesEstimate = nativeBytes;
            _inactiveManagedCountEstimate = managedCount;
        }

        /// <summary>
        /// Aggregate cache occupancy snapshot. Walks every registered
        /// <see cref="IBlobStore"/>'s in-memory cache and sums per-store totals,
        /// then adds <c>ActiveHandleCount</c> from the cache's own handle table.
        /// <para>
        /// Cost: O(in-memory entries across all stores). Intended for
        /// observability — diagnostics overlays, profiler markers, eviction-tuning
        /// dashboards — not for the eviction hot path. Eviction uses a separate
        /// incrementally-maintained estimator (see remarks on
        /// <see cref="BlobCacheSettings.HighWaterMarkMultiplier"/>); calling
        /// <c>GetStats</c> never participates in eviction decisions.
        /// </para>
        /// </summary>
        public BlobCacheStats GetStats()
        {
            AssertMainThreadAndNotDisposed();

            var activeBlobs = new ReadOnlyBlobIdSet(_blobRefCounts);

            long totalNativeBytes = 0;
            long inactiveNativeBytes = 0;
            int totalManagedEntries = 0;
            int inactiveManagedEntries = 0;

            foreach (var store in _stores)
            {
                var storeStats = store.GetStats(activeBlobs);
                totalNativeBytes += storeStats.TotalNativeMemoryBytes;
                inactiveNativeBytes += storeStats.InactiveNativeMemoryBytes;
                totalManagedEntries += storeStats.TotalManagedEntries;
                inactiveManagedEntries += storeStats.InactiveManagedEntries;
            }

            return new BlobCacheStats(
                totalNativeMemoryBytes: totalNativeBytes,
                inactiveNativeMemoryBytes: inactiveNativeBytes,
                totalManagedEntries: totalManagedEntries,
                inactiveManagedEntries: inactiveManagedEntries,
                activeHandleCount: _handles.Count
            );
        }

        /// <summary>
        /// Per-store cache occupancy snapshot. <paramref name="output"/> is cleared
        /// and then populated with one <see cref="BlobStoreStats"/> entry per
        /// registered <see cref="IBlobStore"/>, in the same order as
        /// <see cref="BlobStores"/>. Useful when a project has multiple stores
        /// (e.g. <see cref="BlobStoreInMemory"/> plus a file-backed store) and
        /// you want to see which one is filling up.
        /// <para>
        /// <see cref="BlobCacheStats.ActiveHandleCount"/> is not exposed per-store,
        /// since handles are tracked at the cache level rather than per-store.
        /// </para>
        /// </summary>
        public void GetStatsPerStore(List<BlobStoreStats> output)
        {
            TrecsDebugAssert.IsNotNull(output);
            AssertMainThreadAndNotDisposed();

            output.Clear();

            var activeBlobs = new ReadOnlyBlobIdSet(_blobRefCounts);

            foreach (var store in _stores)
            {
                output.Add(store.GetStats(activeBlobs));
            }
        }

        void MaybeRunInlineEviction()
        {
            if (
                _inactiveNativeBytesEstimate > _nativeBytesHighWaterMark
                || _inactiveManagedCountEstimate > _managedCountHighWaterMark
            )
            {
                _log.Trace(
                    "Inline blob-cache eviction triggered: inactive native bytes "
                        + "estimate {0} > {1} OR inactive managed count estimate "
                        + "{2} > {3}",
                    _inactiveNativeBytesEstimate,
                    _nativeBytesHighWaterMark,
                    _inactiveManagedCountEstimate,
                    _managedCountHighWaterMark
                );
                CleanCaches();
            }
        }

        /// <summary>
        /// Returns true if any registered <see cref="IBlobStore"/> knows about
        /// <paramref name="id"/>, regardless of whether the bytes are managed or native.
        /// </summary>
        public bool Contains(BlobId id, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();
            return TryGetManifestEntry(id, out var _, updateAccessTime: updateAccessTime);
        }

        public bool ContainsManagedBlob(BlobId id, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                TrecsDebugAssert.That(!manifestEntry.IsNative);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if a managed blob exists at <paramref name="id"/> whose stored
        /// type is assignable to <typeparamref name="T"/> (interfaces and base classes
        /// match). Asymmetric with <see cref="ContainsNativeBlob{T}"/>, which requires
        /// exact equality because unmanaged structs cannot have inheritance.
        /// </summary>
        public bool ContainsManagedBlob<T>(BlobId id, bool updateAccessTime = true)
            where T : class
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                return !manifestEntry.IsNative
                    && typeof(T).IsAssignableFrom(TypeId.ToType(manifestEntry.TypeId));
            }

            return false;
        }

        public bool ContainsNativeBlob(BlobId id, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                return manifestEntry.IsNative;
            }

            return false;
        }

        /// <summary>
        /// Returns true if a native blob exists at <paramref name="id"/> whose stored
        /// type is <i>exactly</i> <typeparamref name="T"/>. Asymmetric with
        /// <see cref="ContainsManagedBlob{T}"/>, which allows base-class / interface
        /// matches — unmanaged structs cannot have inheritance, so exact equality is
        /// the only meaningful comparison.
        /// </summary>
        public bool ContainsNativeBlob<T>(BlobId id, bool updateAccessTime = true)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetManifestEntry(id, out var manifestEntry, updateAccessTime: updateAccessTime))
            {
                return manifestEntry.IsNative && manifestEntry.TypeId == TypeId<T>.Value;
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

            TrecsDebugAssert.That(!metadata.IsNative);
            var storedType = TypeId.ToType(metadata.TypeId);
            TrecsDebugAssert.That(
                typeof(T).IsAssignableFrom(storedType),
                "Expected blob assignable to type {0}, but found type {1}",
                typeof(T),
                storedType
            );

            value = (T)valueBase;
            return true;
        }

        internal T GetManagedBlob<T>(BlobId id, bool updateAccessTime = true)
            where T : class
        {
            AssertMainThreadAndNotDisposed();

            var blob = GetBlobAndMetadata(id, out var metadata, updateAccessTime);

            TrecsDebugAssert.That(!metadata.IsNative);
            TrecsDebugAssert.That(typeof(T).IsAssignableFrom(TypeId.ToType(metadata.TypeId)));

            return (T)blob;
        }

        /// <summary>
        /// Allocates a new managed blob under <paramref name="id"/> in the writable
        /// store and returns a pinning <see cref="BlobPtr{T}"/>. Fails if a blob
        /// already exists at this id.
        /// </summary>
        public BlobPtr<T> AllocManagedBlob<T>(BlobId id, T blob)
            where T : class
        {
            AssertMainThreadAndNotDisposed();
            RequireWritableStore().CreateBlobImpl(id, blob, isNative: false);
            return new BlobPtr<T>(CreateHandle(id), id);
        }

        /// <summary>
        /// If a managed blob exists at <paramref name="blobId"/> assignable to
        /// <typeparamref name="T"/>, returns a fresh handle that pins it in the cache;
        /// otherwise returns false. The blob itself must already exist — this is the
        /// lookup path, not an alloc.
        /// </summary>
        public bool TryAcquireBlobPtr<T>(
            BlobId blobId,
            out BlobPtr<T> ptr,
            bool updateAccessTime = true
        )
            where T : class
        {
            if (!ContainsManagedBlob<T>(blobId, updateAccessTime: updateAccessTime))
            {
                ptr = default;
                return false;
            }

            ptr = new BlobPtr<T>(CreateHandle(blobId), blobId);
            return true;
        }

        public bool TryAcquireNativeBlobPtr<T>(BlobId blobId, out NativeBlobPtr<T> ptr)
            where T : unmanaged
        {
            if (!ContainsNativeBlob<T>(blobId))
            {
                ptr = default;
                return false;
            }

            ptr = new NativeBlobPtr<T>(CreateHandle(blobId), blobId);
            return true;
        }

        /// <summary>
        /// Returns a fresh pinning <see cref="BlobPtr{T}"/> for the managed blob at
        /// <paramref name="blobId"/>, throwing if the blob doesn't exist. The blob
        /// itself must already be present — this is the lookup path, not an alloc.
        /// </summary>
        public BlobPtr<T> AcquireBlobPtr<T>(BlobId blobId)
            where T : class
        {
            if (!TryAcquireBlobPtr<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to acquire ptr for unrecognized managed blob {0}",
                    blobId
                );
            }
            return ptr;
        }

        public NativeBlobPtr<T> AcquireNativeBlobPtr<T>(BlobId blobId)
            where T : unmanaged
        {
            if (!TryAcquireNativeBlobPtr<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to acquire ptr for unrecognized native blob {0}",
                    blobId
                );
            }
            return ptr;
        }

        /// <summary>
        /// Returns a type-erased pinning anchor for <paramref name="blobId"/>. Use
        /// this when you only need to keep the blob bytes alive in the cache and
        /// don't care about typed access (e.g. async preload, debug inspectors).
        /// </summary>
        public IBlobAnchor AcquireBlobAnchor(BlobId blobId, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();

            if (
                !TryGetManifestEntry(
                    blobId,
                    out var manifestEntry,
                    updateAccessTime: updateAccessTime
                )
            )
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to get blob anchor for unrecognized native blob {0}",
                    blobId
                );
            }

            var handle = CreateHandle(blobId);
            return new BlobAnchor(handle);
        }

        internal void ForcePurgeBlob(BlobId id)
        {
            RequireWritableStore().ForcePurgeBlob(id);
        }

        internal unsafe bool TryGetNativeBlobPtr<T>(
            BlobId id,
            out IntPtr ptr,
            bool updateAccessTime = true
        )
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();

            if (!TryGetBlobAndMetadata(id, out var blob, out var metadata, updateAccessTime))
            {
                ptr = IntPtr.Zero;
                return false;
            }

            TrecsDebugAssert.That(metadata.IsNative);
            TrecsDebugAssert.That(
                metadata.TypeId == TypeId<T>.Value,
                "Type mismatch retrieving native blob: stored {0}, requested {1}",
                TypeId.ToType(metadata.TypeId),
                typeof(T)
            );

            ptr = ((NativeBlobBox)blob).Ptr;
            return true;
        }

        internal IntPtr GetNativeBlobPtr(BlobId id, int innerTypeId, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();

            _log.Trace("Looking up native blob with inner type id {0}", innerTypeId);

            var blob = GetBlobAndMetadata(id, out var metadata, updateAccessTime);

            TrecsDebugAssert.That(metadata.TypeId.Value == innerTypeId);
            TrecsDebugAssert.That(metadata.IsNative);

            return ((NativeBlobBox)blob).Ptr;
        }

        internal unsafe ref T GetNativeBlobRef<T>(BlobId id, bool updateAccessTime = true)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();

            var blob = GetBlobAndMetadata(id, out var metadata, updateAccessTime);

            TrecsDebugAssert.That(metadata.TypeId == TypeId<T>.Value);
            TrecsDebugAssert.That(metadata.IsNative);

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
            TrecsDebugAssert.That(result, "Attempted to get unrecognized blob id {0}", id);
            return blob;
        }

        internal bool TryGetBlob(BlobId id, out object blob, bool updateAccessTime = true)
        {
            AssertMainThreadAndNotDisposed();

            return TryGetBlobAndMetadata(id, out blob, out _, updateAccessTime);
        }

        public void WarmUpBlob(BlobId id)
        {
            AssertMainThreadAndNotDisposed();

            foreach (var store in _stores)
            {
                if (store.Contains(id))
                {
                    store.WarmUpBlob(id);
                    return;
                }
            }

            throw TrecsDebugAssert.CreateException("No blob store found for id {0}", id);
        }

        public BlobLoadingState GetBlobLoadingState(BlobId id)
        {
            AssertMainThreadAndNotDisposed();

            foreach (var store in _stores)
            {
                if (store.Contains(id))
                {
                    return store.GetBlobLoadingState(id);
                }
            }

            throw TrecsDebugAssert.CreateException("No blob store found for id {0}", id);
        }

        internal PtrHandle CreateHandle(BlobId blobId)
        {
            var id = new PtrHandle(_handleIdCounter);
            _handleIdCounter += 1;
            _handles.Add(id, blobId);

            // Ref-count bookkeeping. Two roles:
            //   1. Detect the 1→0 transition in DisposeHandle that turns a blob
            //      inactive (the high-water-mark estimator update site).
            //   2. The dictionary's keyset *is* the active-blob set used by
            //      GetAllActiveBlobIds and the CleanCaches pass — wrapped as
            //      ReadOnlyBlobIdSet so per-clean rebuild is unnecessary.
            //
            // We deliberately do *not* attempt to subtract from the inactive
            // estimators on a 0→1 transition: at this point we don't yet know
            // whether the blob is actually loaded in any store's memory cache
            // (file/addressable stores load lazily on first
            // TryGetBlobAndMetadata), and over-subtracting would push the
            // estimators below zero. The estimators are re-anchored to truth by
            // RecomputeInactiveTotals after every CleanCaches pass; that bounds
            // any drift to one eviction cycle's worth of churn.
            if (_blobRefCounts.TryGetIndex(blobId, out var idx))
            {
                _blobRefCounts.GetValueAtIndexByRef(idx) += 1;
            }
            else
            {
                _blobRefCounts.Add(blobId, 1);
            }

            return id;
        }

        internal void DisposeHandle(PtrHandle handleId)
        {
            AssertMainThreadAndNotDisposed();

            var blobId = _handles[handleId];
            _handles.RemoveMustExist(handleId);

            ref var refCount = ref _blobRefCounts.GetValueByRef(blobId);
            refCount -= 1;
            TrecsAssert.That(refCount >= 0, "BlobCache ref count went negative for {0}", blobId);

            if (refCount == 0)
            {
                _blobRefCounts.RemoveMustExist(blobId);

                // Last handle released: the blob just transitioned active → inactive
                // (it's still in memory cache; CleanCache is what would actually
                // evict it). Pessimistically add its native bytes / managed count
                // to the running estimators so the high-water check can fire as
                // soon as inline pressure crosses the configured threshold.
                if (TryGetManifestEntry(blobId, out var metadata, updateAccessTime: false))
                {
                    if (metadata.IsNative)
                    {
                        _inactiveNativeBytesEstimate += metadata.NativeBytes;
                    }
                    else
                    {
                        _inactiveManagedCountEstimate += 1;
                    }
                }

                MaybeRunInlineEviction();
            }
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
            AssertMainThreadAndNotDisposed();

            using var _ = TrecsProfiling.Start("BlobCache.TryGetBlobAndMetadata");

            _log.Trace("Attempting to look up blob with id {0}", id);

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

        /// <summary>
        /// Allocates a new native blob under <paramref name="id"/> in the writable
        /// store and returns a pinning <see cref="NativeBlobPtr{T}"/>. Fails if a
        /// blob already exists at this id.
        /// </summary>
        public NativeBlobPtr<T> AllocNativeBlob<T>(BlobId id, in T value)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentFromValue(in value);
            try
            {
                RequireWritableStore().CreateBlobImpl(id, box, isNative: true);
            }
            catch
            {
                box.Dispose();
                throw;
            }
            _log.Trace("Added new blob with id {0} and type {1}", id, typeof(T));
            return new NativeBlobPtr<T>(CreateHandle(id), id);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer and registers it as a blob.
        /// The caller must provide the exact allocation size and alignment so the blob
        /// can be freed correctly. This is critical for variable-sized types where the
        /// allocation is larger than sizeof(T).
        /// See <see cref="NativeUniquePtr.AllocTakingOwnership{T}(WorldAccessor, NativeBlobAllocation, string, int)"/> for the ownership contract.
        /// </summary>
        public NativeBlobPtr<T> AllocNativeBlobTakingOwnership<T>(
            BlobId id,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentTakingOwnership(alloc, typeof(T));
            try
            {
                RequireWritableStore().CreateBlobImpl(id, box, isNative: true);
            }
            catch
            {
                box.Dispose();
                throw;
            }
            _log.Trace(
                "Added new blob (ownership transfer) with id {0} and type {1}",
                id,
                typeof(T)
            );
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
                var seenTypes = new HashSet<TypeId>();
                var typeNames = new StringBuilder();

                foreach (var (_, blobId) in _handles)
                {
                    if (
                        TryGetManifestEntry(blobId, out var manifestEntry, updateAccessTime: false)
                        && seenTypes.Add(manifestEntry.TypeId)
                    )
                    {
                        if (typeNames.Length > 0)
                        {
                            typeNames.Append(", ");
                        }

                        typeNames.Append(TypeId.ToType(manifestEntry.TypeId).GetPrettyName());
                    }
                }

                _log.Warning(
                    "Found {0} undisposed blob handles (types: {1})",
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

        public const long SerializationFlags = 0;
    }
}
