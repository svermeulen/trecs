using System;
using System.Collections.Generic;
using System.Text;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Configuration for the <see cref="BlobCache"/> — the inactive-memory caps that drive
    /// eviction plus the cleanup trigger.
    /// </summary>
    /// <remarks>
    /// Eviction is driven inline by handle-dispose: when the exact running total of inactive
    /// bytes / count crosses the configured cap multiplied by
    /// <see cref="HighWaterMarkMultiplier"/>, a clean pass runs immediately and drains back
    /// down to the configured cap. There is no periodic tick — see <see cref="BlobCache"/>.
    /// <para>
    /// Caps apply only to <i>inactive</i> blobs (those with no live pinning handle). Active
    /// blobs are always retained — eviction can never pull bytes out from under a live pointer.
    /// Evicting a blob only drops its in-memory bytes; the registered source is retained, so the
    /// blob is transparently re-materialized on next access.
    /// </para>
    /// </remarks>
    public sealed class BlobCacheSettings
    {
        /// <summary>
        /// Maximum megabytes of inactive native blobs to retain in memory. The byte cost of a
        /// managed (class) blob is not knowable in C#, so managed blobs are governed separately
        /// by <see cref="MaxInactiveManagedBlobsCount"/>.
        /// </summary>
        public float MaxInactiveNativeBlobsMb { get; init; } = 100f;

        /// <summary>
        /// Maximum number of inactive managed (class) blobs to retain in memory. When the
        /// inactive-managed-blob count exceeds this, the least-recently-used blobs are evicted
        /// first.
        /// </summary>
        /// <remarks>
        /// This is a <i>count</i> because managed object size is not knowable in C#, so it stands
        /// in for a memory cap rather than being one. The default is deliberately conservative: the
        /// two failure modes are asymmetric — too low only costs extra re-materialization of
        /// evicted-then-reused inactive blobs (bounded, and pinned blobs are never evicted), while
        /// too high lets the managed cache grow without an actual memory bound. At the default it
        /// matches <see cref="MaxInactiveNativeBlobsMb"/>'s 100&#160;MB only if managed blobs
        /// average ~400&#160;KB; smaller blobs make the cap effectively non-binding. Raise it for
        /// workloads with many small, expensive-to-rebuild managed blobs that benefit from a deeper
        /// inactive LRU.
        /// </remarks>
        public int MaxInactiveManagedBlobsCount { get; init; } = 256;

        /// <summary>
        /// Multiplier applied to the configured caps to derive the high-water mark that
        /// triggers an inline eviction pass. Values closer to <c>1.0</c> keep memory
        /// tighter to the cap at the cost of more frequent eviction passes; higher
        /// values amortize the eviction cost over more allocations at the cost of
        /// larger transient overshoot. Must be &gt;= <c>1.0</c>.
        /// </summary>
        public float HighWaterMarkMultiplier { get; init; } = 1.5f;

        public static readonly BlobCacheSettings Default = new();
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Aggregate snapshot of <see cref="BlobCache"/> occupancy. Returned by
    /// <see cref="BlobCache.GetStats"/>. Use this to tune the cache's configured
    /// caps (<see cref="BlobCacheSettings.MaxInactiveNativeBlobsMb"/> /
    /// <see cref="BlobCacheSettings.MaxInactiveManagedBlobsCount"/>) and to diagnose
    /// cache thrashing.
    /// <para>
    /// "Total" and "Inactive" cover only entries currently resident in memory.
    /// </para>
    /// </summary>
    public readonly struct BlobCacheStats
    {
        /// <summary>
        /// Total bytes of native blobs currently resident in memory, including both
        /// active and inactive entries.
        /// </summary>
        public readonly long TotalNativeMemoryBytes;

        /// <summary>
        /// Bytes of native blobs resident in memory that are not currently pinned by any
        /// handle. This is the figure compared against the <c>MaxInactiveNativeBlobsMb</c>
        /// cap to decide whether an inline eviction pass needs to fire.
        /// </summary>
        public readonly long InactiveNativeMemoryBytes;

        /// <summary>
        /// Total number of managed (class) blobs currently resident in memory, including
        /// both active and inactive entries.
        /// </summary>
        public readonly int TotalManagedEntries;

        /// <summary>
        /// Number of managed blobs resident in memory that are not currently pinned by any
        /// handle. Compared against the <c>MaxInactiveManagedBlobsCount</c> cap on the
        /// eviction-trigger path.
        /// </summary>
        public readonly int InactiveManagedEntries;

        /// <summary>
        /// Number of outstanding pinning handles (<see cref="SharedAnchor{T}"/>,
        /// <see cref="NativeSharedAnchor{T}"/>, <see cref="IBlobAnchor"/>) currently
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
    /// Pure resident store for shared blob data: materialized bytes for the blobs currently
    /// resident in memory, plus reference counting, inline LRU eviction, and eager allocation. It
    /// holds no blob <i>sources</i> and no global manifest — it cannot materialize a blob on its
    /// own. Registering and (re-)materializing a blob from its source is the job of the
    /// <see cref="BlobFactory"/> layer above; the factory materializes and then <see cref="Insert"/>s
    /// the bytes here, and pins via <see cref="CreateHandle"/>.
    /// <para>
    /// The governing invariant is <b>eager-on-acquire residency</b>: any blob with at least one live
    /// handle is resident in the in-memory cache, so a typed <c>Get</c> is always a pure resident
    /// lookup. <see cref="CreateHandle"/> therefore asserts residency rather than materializing.
    /// </para>
    /// <para>
    /// Eviction is driven by the handle-dispose hot path: an exact running total of inactive
    /// bytes / count is updated incrementally, and when it crosses the high-water mark (see
    /// <see cref="BlobCacheSettings.HighWaterMarkMultiplier"/>) a clean pass runs immediately and
    /// drains back down to the configured cap. Only dispose can grow the inactive total (acquire
    /// makes a blob active, which can only shrink it), so eviction is checked there alone. Evicting
    /// a blob drops its bytes <i>and</i> its resident-metadata entry; a sourced blob is transparently
    /// re-materialized by the factory on next acquire, an eager blob is forgotten for good.
    /// </para>
    /// </summary>
    public sealed class BlobCache : IDisposable
    {
        readonly TrecsLog _log;

        readonly IterableDictionary<PtrHandle, BlobId> _handles = new();
        readonly IterableDictionary<BlobId, int> _blobRefCounts = new();
        readonly BlobCacheSettings _settings;
        readonly NativeBlobBoxPool _nativeBlobBoxPool;

        // The resident store: one entry per blob currently resident in memory, holding both the
        // materialized bytes and the per-blob metadata. Only resident blobs are tracked here; the
        // source registry that knows how to (re-)materialize an evicted or never-yet-materialized
        // blob lives on the BlobFactory, above this layer.
        readonly IterableDictionary<BlobId, ResidentEntry> _resident = new();

        // Running count of resident entries with Meta.IsEager set, maintained at the two points
        // entries enter/leave _resident (InsertResident / RemoveResident). Lets the snapshot
        // serializer's opaque-ref section skip its per-referenced-id metadata probes with one O(1)
        // check in the common no-eager-blobs case — heap-referenced blobs are pinned (⇒ resident),
        // so zero eager residents means zero eager references. Paid every rollback-frame save.
        int _numEagerResident;

#if DEBUG
        // Cross-content collision guard (DEBUG only). Records a fingerprint — type, native-ness,
        // byte size, and (where computable) a 64-bit content hash — the first time each id is made
        // resident, and survives eviction. Catches two silent desync vectors at the insert that
        // introduces them: the same BlobId reused for a *different* blob across its lifetime (the
        // explicit-id escape hatch), and an *impure builder* re-materializing different bytes for
        // the same id after eviction. The latter matters because for derivable blobs the id is the
        // descriptor hash, not the content hash — divergent rebuilt content is otherwise invisible
        // to snapshot checksums. Content hashing covers native blobs always (their bytes are in
        // hand) and managed blobs when a serializer is registered for the runtime type (installed
        // by WorldBuilder via SetDebugManagedContentHasher); unhashable managed blobs fall back to
        // the structural fingerprint. Stripped from release builds.
        readonly Dictionary<BlobId, IdFingerprint> _idFingerprints = new();

        // Hashes a managed blob to a 64-bit content hash, or returns 0 when the blob's runtime
        // type has no registered serializer (attestation skipped for that blob). Installed by
        // WorldBuilder; null for bare-cache tests, which then attest native blobs only.
        Func<object, ulong> _debugManagedContentHasher;

        internal void SetDebugManagedContentHasher(Func<object, ulong> hasher)
        {
            _debugManagedContentHasher = hasher;
        }

        readonly struct IdFingerprint
        {
            public readonly TypeId TypeId;
            public readonly bool IsNative;
            public readonly long NativeBytes;

            // 0 = content hash unavailable for this insert (unhashable managed type, or no
            // hasher installed). A real hash of 0 cannot occur (hashers reserve it).
            public readonly ulong ContentHash;

            public IdFingerprint(TypeId typeId, bool isNative, long nativeBytes, ulong contentHash)
            {
                TypeId = typeId;
                IsNative = isNative;
                NativeBytes = nativeBytes;
                ContentHash = contentHash;
            }

            public bool StructureEquals(in IdFingerprint other) =>
                TypeId == other.TypeId
                && IsNative == other.IsNative
                && NativeBytes == other.NativeBytes;

            public override string ToString() =>
                $"{(IsNative ? "native" : "managed")} {TypeId.ToType(TypeId)} "
                + $"({NativeBytes} bytes, content hash {ContentHash:X16})";
        }
#endif

        // ─── Inactive-blob LRU (intrusive, O(1) maintenance) ───────────────
        //
        // Eviction order is captured by two intrusive doubly-linked lists threaded through the
        // resident entries themselves (the prev/next links live on ResidentEntry, keyed by BlobId
        // so they survive the dictionary's swap-remove) — one list for native blobs (governed by
        // bytes), one for managed (governed by count). A list holds exactly the resident blobs that
        // are currently inactive; Head is the least-recently-used end, Tail the most-recently-used.
        //
        // The order is fixed at the active→inactive transition, which is sound because an inactive
        // blob is never read: under the eager-on-acquire invariant a typed Get only ever touches a
        // pinned (⇒ active ⇒ unlisted) blob, so nothing can reorder the list between deactivation
        // and eviction. "Use" is therefore a pin cycle, not an individual read: acquiring unlinks a
        // blob (it leaves the inactive set), releasing its last handle appends it at the MRU tail.
        // Eviction walks from Head, so the blob whose pin was dropped longest ago goes first.
        //
        // List membership is kept perfectly in lockstep with the inactive running totals below:
        // every AddToInactiveTotals is paired with an LruAppend, every RemoveFromInactiveTotals with
        // an LruUnlink. So "in the list" ≡ "counted in the inactive totals" ≡ "resident and
        // inactive", and eviction never has to walk the whole cache or sort.
        struct LruList
        {
            public BlobId Head;
            public BlobId Tail;
        }

        LruList _nativeLru;
        LruList _managedLru;

        // High-water marks derived from the configured caps × multiplier. When the running
        // total below crosses either, an inline CleanCaches pass runs and drains back to the cap.
        readonly long _nativeBytesHighWaterMark;
        readonly int _managedCountHighWaterMark;

        // Exact running total of inactive native bytes / inactive managed blob count resident in
        // memory. Maintained incrementally and exactly: a blob is added when it becomes
        // resident-while-inactive (materialize or AddEagerBlob) or transitions active→inactive
        // (last handle disposed), and subtracted when it transitions inactive→active (first handle
        // acquired) or is evicted. This is exact — not an estimate — because the eager-on-acquire
        // invariant guarantees an active blob is always resident, so its byte size is known at
        // every transition. Re-anchored to truth after every CleanCaches pass (CleanMemoryCache
        // returns the post-eviction remainder) as a cheap self-heal backstop.
        long _inactiveNativeBytes;
        int _inactiveManagedCount;

        uint _handleIdCounter = 1;
        bool _hasDisposed;

        public BlobCache(
            TrecsLog log,
            BlobCacheSettings settings,
            NativeBlobBoxPool nativeBlobBoxPool
        )
        {
            TrecsDebugAssert.IsNotNull(nativeBlobBoxPool);
            _log = log;
            _settings = settings ?? BlobCacheSettings.Default;
            _nativeBlobBoxPool = nativeBlobBoxPool;

            TrecsAssert.That(
                _settings.HighWaterMarkMultiplier >= 1f,
                "BlobCacheSettings.HighWaterMarkMultiplier must be >= 1.0, was {0}",
                _settings.HighWaterMarkMultiplier
            );
            TrecsAssert.That(
                _settings.MaxInactiveNativeBlobsMb >= 0,
                "MaxInactiveNativeBlobsMb must be non-negative, was {0}",
                _settings.MaxInactiveNativeBlobsMb
            );
            TrecsAssert.That(
                _settings.MaxInactiveManagedBlobsCount >= 0,
                "MaxInactiveManagedBlobsCount must be non-negative, was {0}",
                _settings.MaxInactiveManagedBlobsCount
            );

            long nativeBytesCap = (long)(_settings.MaxInactiveNativeBlobsMb * 1024f * 1024f);

            // Compute in double so we don't lose precision for large caps (long-to-float
            // round-trips at ~7 significant digits), and clamp before narrowing.
            _nativeBytesHighWaterMark = (long)
                Math.Min((double)nativeBytesCap * _settings.HighWaterMarkMultiplier, long.MaxValue);
            _managedCountHighWaterMark = (int)
                Math.Min(
                    (double)_settings.MaxInactiveManagedBlobsCount
                        * _settings.HighWaterMarkMultiplier,
                    int.MaxValue
                );
        }

        // BlobCache is a main-thread-only abstraction. Asserts both invariants
        // (alive + on the right thread) at the top of every method so a
        // misuse from a job surfaces immediately instead of corrupting state.
        void AssertMainThreadAndNotDisposed()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread, "BlobCache is main-thread only");
        }

        // Exposed so the BlobFactory's native descriptor factories (and the svkj opaque loader)
        // can hand the per-world pool to the native sources they construct — native sources own
        // the pool they rent boxes from, keeping it out of the IBlobSource.Materialize contract.
        internal NativeBlobBoxPool NativeBlobBoxPool => _nativeBlobBoxPool;

        // ─── Resident metadata / type introspection ─────────────────────

        bool TryGetResidentMeta(BlobId id, out BlobMetadata meta)
        {
            AssertMainThreadAndNotDisposed();

            if (_resident.TryGetIndex(id, out var index))
            {
                meta = _resident.GetValueAtIndexByRef(index).Meta;
                return true;
            }

            meta = default;
            return false;
        }

        /// <summary>
        /// Reports the resident blob's stored type id and native-ness, for the
        /// <see cref="BlobFactory"/>'s acquire-time type gate. Returns false if the blob is not
        /// resident — the factory then falls back to its source registry.
        /// </summary>
        internal bool TryGetResidentTypeInfo(BlobId id, out TypeId typeId, out bool isNative)
        {
            if (TryGetResidentMeta(id, out var meta))
            {
                typeId = meta.TypeId;
                isNative = meta.IsNative;
                return true;
            }

            typeId = default;
            isNative = false;
            return false;
        }

        internal Type TryGetManagedBlobType(BlobId id)
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetResidentMeta(id, out var meta))
            {
                TrecsDebugAssert.That(!meta.IsNative);
                return TypeId.ToType(meta.TypeId);
            }

            return null;
        }

        internal Type GetManagedBlobType(BlobId id)
        {
            var result = TryGetManagedBlobType(id);
            TrecsDebugAssert.IsNotNull(result);
            return result;
        }

        internal Type TryGetNativeBlobType(BlobId id)
        {
            AssertMainThreadAndNotDisposed();

            if (TryGetResidentMeta(id, out var meta))
            {
                TrecsDebugAssert.That(meta.IsNative);
                return TypeId.ToType(meta.TypeId);
            }

            return null;
        }

        internal Type GetNativeBlobType(BlobId id)
        {
            var result = TryGetNativeBlobType(id);
            TrecsDebugAssert.IsNotNull(result);
            return result;
        }

        /// <summary>
        /// Copy every <see cref="BlobId"/> currently pinned by at least one outstanding
        /// handle into <paramref name="blobIds"/>. The output is the deduplicated set
        /// of active blobs. O(active blobs); maintained incrementally on every handle
        /// create/dispose, not by walking the full handle table.
        /// </summary>
        public void GetAllActiveBlobIds(IterableHashSet<BlobId> blobIds)
        {
            AssertMainThreadAndNotDisposed();

            foreach (var blobId in _blobRefCounts.Keys)
            {
                blobIds.Add(blobId);
            }
        }

        // ─── Eviction ───────────────────────────────────────────────────

        /// <summary>
        /// Run an eviction pass over the in-memory cache and re-anchor the inactive-bytes /
        /// inactive-count running totals to truth. Called inline from the handle-dispose hot
        /// path when either total crosses the high-water mark.
        /// <para><b>Main-thread only</b> and must not be called while a job is reading from the
        /// in-memory cache — a concurrent eviction can free entries that jobs may be reading via
        /// <see cref="NativeSharedPtrResolver"/>.</para>
        /// </summary>
        public void CleanCaches()
        {
            AssertMainThreadAndNotDisposed();

#if DEBUG
            VerifyLruConsistency();
#endif

            // Eviction walks the inactive LRU lists from their LRU ends and hands back the
            // post-eviction inactive remainder, so the running totals re-anchor to truth without a
            // full-cache walk.
            CleanMemoryCache(
                out var remainingInactiveNativeBytes,
                out var remainingInactiveManagedCount
            );

            _inactiveNativeBytes = remainingInactiveNativeBytes;
            _inactiveManagedCount = remainingInactiveManagedCount;
        }

        /// <summary>
        /// Aggregate cache occupancy snapshot. Walks the in-memory cache once.
        /// Intended for observability — diagnostics overlays, profiler markers,
        /// eviction-tuning dashboards — not the eviction hot path.
        /// </summary>
        public BlobCacheStats GetStats()
        {
            AssertMainThreadAndNotDisposed();

            long totalNativeBytes = 0;
            long inactiveNativeBytes = 0;
            int totalManagedEntries = 0;
            int inactiveManagedEntries = 0;

            foreach (var (blobId, entry) in _resident)
            {
                var meta = entry.Meta;
                // The ref-count dictionary's keyset is the active-blob set: a blob is active iff at
                // least one handle pins it.
                bool isInactive = !_blobRefCounts.ContainsKey(blobId);

                if (meta.IsNative)
                {
                    totalNativeBytes += meta.NativeBytes;

                    if (isInactive)
                    {
                        inactiveNativeBytes += meta.NativeBytes;
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

            return new BlobCacheStats(
                totalNativeMemoryBytes: totalNativeBytes,
                inactiveNativeMemoryBytes: inactiveNativeBytes,
                totalManagedEntries: totalManagedEntries,
                inactiveManagedEntries: inactiveManagedEntries,
                activeHandleCount: _handles.Count
            );
        }

        // ─── LRU / eviction internals ───────────────────────────────────

        void DisposeBlob(object blob)
        {
            // The stored object's runtime type is the discriminant: native blobs own a
            // NativeBlobBox allocation that must be released; managed blobs are created via their
            // factory (never pooled), so dropping the reference and letting the GC reclaim them is
            // all that's needed.
            if (blob is NativeBlobBox box)
            {
                box.Dispose();
            }
        }

        /// <summary>
        /// Evicts inactive (unpinned) blobs to bring the cache back under its limits. Native and
        /// managed blobs are governed separately — native by total bytes, managed by entry count
        /// (managed object size is not knowable in C#) — each walking its own inactive LRU list
        /// from the least-recently-used (<c>Head</c>) end. Active blobs are never in a list, so they
        /// are never evicted.
        /// <para>
        /// Evicting a blob drops both its in-memory bytes and its resident-metadata entry. A sourced
        /// blob is transparently re-materialized by the <see cref="BlobFactory"/> on next acquire;
        /// an eager blob (no source) is forgotten for good.
        /// </para>
        /// <para>
        /// Reports the post-eviction inactive remainder via
        /// <paramref name="remainingInactiveNativeBytes"/> /
        /// <paramref name="remainingInactiveManagedCount"/> — the exact running totals minus what
        /// each pass removed — so <see cref="CleanCaches"/> can re-anchor its inactive totals. The
        /// work is O(evicted), not O(resident): no full-cache walk and no sort.
        /// </para>
        /// </summary>
        void CleanMemoryCache(
            out long remainingInactiveNativeBytes,
            out int remainingInactiveManagedCount
        )
        {
            long maxNativeBytes = (long)(_settings.MaxInactiveNativeBlobsMb * 1024f * 1024f);
            remainingInactiveNativeBytes = EvictNativeLru(maxNativeBytes);
            remainingInactiveManagedCount = EvictManagedLru(_settings.MaxInactiveManagedBlobsCount);
        }

        // Walks the native inactive list from its LRU end, evicting until inactive native bytes are
        // back under the cap. Returns the post-eviction inactive native byte total.
        long EvictNativeLru(long maxBytes)
        {
            long inactiveBytes = _inactiveNativeBytes;
            int numRemoved = 0;

            var cur = _nativeLru.Head;
            while (inactiveBytes > maxBytes && !cur.IsNull)
            {
                ref var entry = ref _resident.GetValueByRef(cur);
                var next = entry.LruNext;
                long bytes = entry.Meta.NativeBytes;

                _log.Trace("Disposing native blob {0}", cur);
                EvictInactive(cur, isNative: true);

                inactiveBytes -= bytes;
                numRemoved += 1;
                cur = next;
            }

            if (numRemoved > 0)
            {
                _log.Debug(
                    "Removed {0} native blobs from in-memory cache to get under byte limit",
                    numRemoved
                );
            }

            return inactiveBytes;
        }

        // Walks the managed inactive list from its LRU end, evicting until the inactive managed
        // count is back under the cap. Returns the post-eviction inactive managed count.
        int EvictManagedLru(int maxCount)
        {
            int inactiveCount = _inactiveManagedCount;
            int numRemoved = 0;

            var cur = _managedLru.Head;
            while (inactiveCount > maxCount && !cur.IsNull)
            {
                ref var entry = ref _resident.GetValueByRef(cur);
                var next = entry.LruNext;

                _log.Trace("Disposing managed blob {0}", cur);
                EvictInactive(cur, isNative: false);

                inactiveCount -= 1;
                numRemoved += 1;
                cur = next;
            }

            if (numRemoved > 0)
            {
                _log.Debug(
                    "Removed {0} managed blobs from in-memory cache to get under count limit",
                    numRemoved
                );
            }

            return inactiveCount;
        }

        // Unlinks an inactive blob from its LRU list, drops its resident entry, and disposes its
        // bytes. The caller accounts for the inactive-total decrement (CleanCaches re-anchors from
        // the returned remainder). Must read any needed fields off the entry before this call —
        // RemoveResident swap-removes, invalidating refs into the resident array.
        void EvictInactive(BlobId id, bool isNative)
        {
            LruUnlink(id, isNative);
            RemoveResident(id, out var blob);
            DisposeBlob(blob);
        }

        /// <summary>
        /// Targeted eviction of a single blob, used when its id is being forgotten outright (the
        /// <see cref="BlobFactory"/> input-descriptor sweep) rather than to satisfy a cap. A no-op
        /// returning false if the blob is not resident or still pinned by a handle — unlike the
        /// cap-driven walk, the caller doesn't get to choose the moment the last pin releases, so
        /// a still-active blob just ages out through the normal inactive caps later.
        /// </summary>
        internal bool TryEvictInactive(BlobId id)
        {
            AssertMainThreadAndNotDisposed();

            if (!_resident.ContainsKey(id) || _blobRefCounts.ContainsKey(id))
            {
                return false;
            }

            // Copy the metadata out first — EvictInactive swap-removes the resident entry. The
            // targeted form must also decrement the inactive running totals itself; the cap-driven
            // CleanCaches path instead re-anchors them from its walk's remainder.
            var meta = _resident.GetValueByRef(id).Meta;
            if (meta.IsNative)
            {
                _inactiveNativeBytes -= meta.NativeBytes;
            }
            else
            {
                _inactiveManagedCount -= 1;
            }
            EvictInactive(id, meta.IsNative);
            return true;
        }

        // Drops a blob's resident entry (bytes + metadata) and hands back the bytes for disposal.
        void RemoveResident(BlobId id, out object blob)
        {
            var wasRemoved = _resident.TryRemove(id, out var entry);
            TrecsDebugAssert.That(wasRemoved);
            blob = entry.Blob;
            if (entry.Meta.IsEager)
            {
                TrecsDebugAssert.That(_numEagerResident > 0);
                _numEagerResident--;
            }
        }

        // One resident blob: its materialized bytes, metadata, and — while inactive — its links in
        // the inactive LRU list (BlobId.Null when active/unlisted). Stored together so the bytes and
        // metadata can never desync and a typed read resolves both in a single dictionary lookup.
        struct ResidentEntry
        {
            public object Blob;
            public BlobMetadata Meta;

            // Intrusive doubly-linked-list links into _nativeLru / _managedLru. Keyed by BlobId (not
            // a slot index) so they survive the resident dictionary's swap-remove. Both BlobId.Null
            // while the blob is active (not in any list).
            public BlobId LruPrev;
            public BlobId LruNext;
        }

        ref LruList LruFor(bool isNative)
        {
            if (isNative)
            {
                return ref _nativeLru;
            }
            return ref _managedLru;
        }

        // Appends a now-inactive blob at the MRU (Tail) end of its list. Mirrors AddToInactiveTotals.
        void LruAppend(BlobId id, bool isNative)
        {
            ref var list = ref LruFor(isNative);
            ref var entry = ref _resident.GetValueByRef(id);

            entry.LruPrev = list.Tail;
            entry.LruNext = BlobId.Null;

            if (list.Tail.IsNull)
            {
                list.Head = id;
            }
            else
            {
                _resident.GetValueByRef(list.Tail).LruNext = id;
            }

            list.Tail = id;
        }

        // Removes a blob from its inactive list (it became active, or is being evicted). Mirrors
        // RemoveFromInactiveTotals. No structural change to _resident, so the held refs stay valid.
        void LruUnlink(BlobId id, bool isNative)
        {
            ref var list = ref LruFor(isNative);
            ref var entry = ref _resident.GetValueByRef(id);

            var prev = entry.LruPrev;
            var next = entry.LruNext;

            if (prev.IsNull)
            {
                list.Head = next;
            }
            else
            {
                _resident.GetValueByRef(prev).LruNext = next;
            }

            if (next.IsNull)
            {
                list.Tail = prev;
            }
            else
            {
                _resident.GetValueByRef(next).LruPrev = prev;
            }

            entry.LruPrev = BlobId.Null;
            entry.LruNext = BlobId.Null;
        }

#if DEBUG
        // Self-check (DEBUG only): the intrusive lists are maintained incrementally, so this asserts
        // they still agree with the exact inactive running totals — native list bytes sum to
        // _inactiveNativeBytes, managed list length equals _inactiveManagedCount, and every listed
        // blob is resident-and-inactive of the right kind. Replaces the old re-tally-from-scratch
        // that the walk-based eviction did for free.
        void VerifyLruConsistency()
        {
            // A corrupt link could form a cycle; bound each walk by the resident count so the
            // verifier asserts instead of hanging. No list can be longer than the resident set.
            int maxSteps = _resident.Count;

            long nativeBytes = 0;
            int nativeSteps = 0;
            for (var cur = _nativeLru.Head; !cur.IsNull; )
            {
                TrecsDebugAssert.That(++nativeSteps <= maxSteps, "Cycle in native LRU list");
                ref var entry = ref _resident.GetValueByRef(cur);
                TrecsDebugAssert.That(
                    entry.Meta.IsNative,
                    "Managed blob {0} in native LRU list",
                    cur
                );
                TrecsDebugAssert.That(
                    !_blobRefCounts.ContainsKey(cur),
                    "Active blob {0} in native LRU list",
                    cur
                );
                nativeBytes += entry.Meta.NativeBytes;
                cur = entry.LruNext;
            }

            int managedCount = 0;
            for (var cur = _managedLru.Head; !cur.IsNull; )
            {
                TrecsDebugAssert.That(managedCount < maxSteps, "Cycle in managed LRU list");
                ref var entry = ref _resident.GetValueByRef(cur);
                TrecsDebugAssert.That(
                    !entry.Meta.IsNative,
                    "Native blob {0} in managed LRU list",
                    cur
                );
                TrecsDebugAssert.That(
                    !_blobRefCounts.ContainsKey(cur),
                    "Active blob {0} in managed LRU list",
                    cur
                );
                managedCount += 1;
                cur = entry.LruNext;
            }

            // Native list is byte-governed (no count to compare); managed list is count-governed.
            TrecsDebugAssert.That(
                nativeBytes == _inactiveNativeBytes,
                "Native LRU list bytes {0} != inactive total {1}",
                nativeBytes,
                _inactiveNativeBytes
            );
            TrecsDebugAssert.That(
                managedCount == _inactiveManagedCount,
                "Managed LRU list count {0} != inactive total {1}",
                managedCount,
                _inactiveManagedCount
            );
        }
#endif

        void MaybeRunInlineEviction()
        {
            if (
                _inactiveNativeBytes > _nativeBytesHighWaterMark
                || _inactiveManagedCount > _managedCountHighWaterMark
            )
            {
                _log.Trace(
                    "Inline blob-cache eviction triggered: inactive native bytes "
                        + "{0} > {1} OR inactive managed count {2} > {3}",
                    _inactiveNativeBytes,
                    _nativeBytesHighWaterMark,
                    _inactiveManagedCount,
                    _managedCountHighWaterMark
                );
                CleanCaches();
            }
        }

        // The two inactive running totals AND the inactive LRU lists are kept exact by mirrored
        // add/remove at every transition that changes a blob's resident-and-inactive status, so the
        // two can never drift apart. Native blobs contribute their known byte size; managed blobs
        // (size unknowable in C#) contribute a count of 1. The blob must already be resident.
        void AddToInactiveTotals(BlobId id, bool isNative, long nativeBytes)
        {
            if (isNative)
            {
                _inactiveNativeBytes += nativeBytes;
            }
            else
            {
                _inactiveManagedCount += 1;
            }
            LruAppend(id, isNative);
        }

        void RemoveFromInactiveTotals(BlobId id, bool isNative, long nativeBytes)
        {
            if (isNative)
            {
                _inactiveNativeBytes -= nativeBytes;
            }
            else
            {
                _inactiveManagedCount -= 1;
            }
            LruUnlink(id, isNative);
        }

        // ─── Residency / insertion ──────────────────────────────────────

        /// <summary>
        /// Returns true if the blob's bytes are currently resident in memory. This is a pure
        /// membership test — the cache has no knowledge of registered-but-not-resident blobs (that
        /// lives on the <see cref="BlobFactory"/>'s source registry).
        /// </summary>
        internal bool IsResident(BlobId id)
        {
            AssertMainThreadAndNotDisposed();
            return _resident.ContainsKey(id);
        }

        /// <summary>
        /// True if any resident blob is eager (opaque-alloc'd — no descriptor/factory to re-derive
        /// its bytes). O(1). When false, no <i>referenced</i> blob can be eager either (referenced
        /// blobs are pinned, and pinned ⇒ resident), which lets the snapshot serializer's
        /// opaque-ref section skip its per-referenced-id metadata probes on every save.
        /// </summary>
        internal bool HasEagerResidentBlobs
        {
            get
            {
                AssertMainThreadAndNotDisposed();
                return _numEagerResident > 0;
            }
        }

        /// <summary>
        /// Inserts a freshly-materialized <i>sourced</i> blob as resident, carrying its
        /// <paramref name="declaredTypeId"/> (which may be an interface/base type for a managed
        /// blob — not the resident object's concrete type). The <see cref="BlobFactory"/> calls this
        /// after materializing from a source and after its own re-entrancy guard. Asserts the id is
        /// not already resident.
        /// </summary>
        internal void Insert(BlobId id, object blob, TypeId declaredTypeId, long nativeBytes)
        {
            AssertMainThreadAndNotDisposed();
            bool isNative = blob is NativeBlobBox;
            InsertResident(id, blob, declaredTypeId, isNative, isEager: false, nativeBytes);
            _log.Trace("Inserted sourced blob {0} of type {1}", id, TypeId.ToType(declaredTypeId));
        }

        // ─── Eager allocation (internal) ────────────────────────────────
        //
        // Used for blobs whose bytes already exist at creation time and that are NOT
        // re-creatable from a registered source: input-pipeline (frame-scoped) payloads and
        // BlobBuilder output. These have no source on the factory, so they linger as inactive until
        // LRU-evicted, at which point they are forgotten entirely (resident entry dropped along with
        // the bytes). Persistent, re-materializable blobs are registered on the BlobFactory instead.

        /// <summary>
        /// Eagerly stores an already-built managed <paramref name="blob"/> under <paramref name="id"/>
        /// and returns a pinning handle. The bytes exist up front and are NOT re-creatable from a
        /// registered source, so the entry lingers until LRU-evicted and is then forgotten (re-store
        /// it the same way on a later miss). For computed-once results (background-task output, baked
        /// level data); cheap, re-derivable managed blobs should use the source registry instead.
        /// </summary>
        public SharedAnchor<T> AllocManagedBlob<T>(BlobId id, T blob)
            where T : class
        {
            AssertMainThreadAndNotDisposed();
            AddEagerBlob(id, blob);
            return new SharedAnchor<T>(CreateHandle(id), id);
        }

        internal NativeSharedAnchor<T> AllocNativeBlob<T>(BlobId id, in T value)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentFromValue(in value);
            try
            {
                AddEagerBlob(id, box);
            }
            catch
            {
                box.Dispose();
                throw;
            }
            _log.Trace("Eagerly added native blob with id {0} and type {1}", id, typeof(T));
            return new NativeSharedAnchor<T>(CreateHandle(id), id);
        }

        /// <summary>
        /// Eagerly stores an already-built native <paramref name="alloc"/> under <paramref name="id"/>,
        /// taking ownership, and returns a pinning handle. The bytes exist up front and are NOT
        /// re-creatable from a registered source, so the entry lingers until LRU-evicted and is then
        /// forgotten. The <see cref="NativeSharedPtr.Alloc{T}(WorldAccessor, BlobId, NativeBlobAllocation)"/> heap-layer wrapper is
        /// the usual entry point; this cache-layer form is for callers that hold a
        /// <see cref="BlobCache"/> directly (e.g. the level-bake pipeline).
        /// </summary>
        public NativeSharedAnchor<T> AllocNativeBlobTakingOwnership<T>(
            BlobId id,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentTakingOwnership(alloc, typeof(T));
            try
            {
                AddEagerBlob(id, box);
            }
            catch
            {
                box.Dispose();
                throw;
            }
            _log.Trace(
                "Eagerly added native blob (ownership transfer) with id {0} and type {1}",
                id,
                typeof(T)
            );
            return new NativeSharedAnchor<T>(CreateHandle(id), id);
        }

        /// <summary>
        /// Content-addressed eager native blob: derives the <see cref="BlobId"/> from the value's raw
        /// bytes (xxHash64) rather than a caller-supplied id, then stores it like
        /// <see cref="AllocNativeBlob{T}(BlobId, in T)"/>. Identical content yields the same id, so a
        /// re-alloc of equal bytes deduplicates to the existing blob instead of throwing. This is the
        /// id derivation the opaque-blob store relies on for cross-snapshot/recording dedup and stable
        /// cross-machine ids.
        /// </summary>
        internal NativeSharedAnchor<T> AllocNativeBlob<T>(in T value)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentFromValue(in value);
            try
            {
                return CreateContentAddressedHandle<T>(box);
            }
            catch
            {
                box.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Content-addressed counterpart to
        /// <see cref="AllocNativeBlobTakingOwnership{T}(BlobId, NativeBlobAllocation)"/>: the id is the
        /// xxHash64 of <paramref name="alloc"/>'s bytes. Equal content deduplicates to the existing
        /// blob (and frees <paramref name="alloc"/>) rather than throwing on a duplicate id.
        /// </summary>
        public NativeSharedAnchor<T> AllocNativeBlobTakingOwnership<T>(NativeBlobAllocation alloc)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentTakingOwnership(alloc, typeof(T));
            try
            {
                return CreateContentAddressedHandle<T>(box);
            }
            catch
            {
                box.Dispose();
                throw;
            }
        }

        // Hashes the box's bytes to a content id; if a blob already exists there (same content, or a
        // collision) returns a handle to it and disposes the freshly-rented box, otherwise stores the
        // box eagerly under the content id. Caller owns the box on entry and on the throwing path.
        NativeSharedAnchor<T> CreateContentAddressedHandle<T>(NativeBlobBox box)
            where T : unmanaged
        {
            var id = EnsureResidentContentAddressed(box);
            return new NativeSharedAnchor<T>(CreateHandle(id), id);
        }

        // Derives the content id from the box's bytes (xxHash64) and makes it resident WITHOUT
        // pinning: inserts the box eagerly on a miss, disposes it on a hit (dedup). Returns the
        // content id; the caller pins — a cache handle for an anchor, or an ECS heap entry. Caller
        // owns the box on the throwing path.
        unsafe BlobId EnsureResidentContentAddressed(NativeBlobBox box)
        {
            var id = BlobIdGenerator.FromBytes(
                new ReadOnlySpan<byte>(box.Ptr.ToPointer(), box.Size)
            );
            if (IsResident(id))
            {
                // Same content (eager blobs are resident-or-forgotten, so a hit means resident).
                box.Dispose();
                return id;
            }
            AddEagerBlob(id, box);
            return id;
        }

        /// <summary>
        /// Content-addressed eager native insert WITHOUT a pin: derives the id from
        /// <paramref name="value"/>'s bytes, makes it resident on a miss (dedup on a hit), and returns
        /// the id. The ECS-layer <see cref="NativeSharedPtr.Alloc{T}(WorldAccessor, in T)"/> uses this
        /// and then pins via the heap; the anchor path pins via <see cref="AllocNativeBlob{T}(in T)"/>.
        /// </summary>
        internal BlobId EnsureNativeBlobContentAddressed<T>(in T value)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentFromValue(in value);
            try
            {
                return EnsureResidentContentAddressed(box);
            }
            catch
            {
                box.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Taking-ownership counterpart to <see cref="EnsureNativeBlobContentAddressed{T}(in T)"/>:
        /// content id from <paramref name="alloc"/>'s bytes, resident-without-pin, returns the id.
        /// </summary>
        internal BlobId EnsureNativeBlobContentAddressedTakingOwnership<T>(
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            var box = _nativeBlobBoxPool.RentTakingOwnership(alloc, typeof(T));
            try
            {
                return EnsureResidentContentAddressed(box);
            }
            catch
            {
                box.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Inserts an already-built opaque blob under <paramref name="id"/> as eager, <i>without</i>
        /// returning a pinning handle — the caller pins separately (the heaps re-pin by id via
        /// <see cref="CreateHandle"/>). Two callers: the snapshot/recording load path, making a
        /// persisted opaque blob resident before the heaps re-pin it; and content-addressed managed
        /// <c>Alloc</c>, seeding the freshly-hashed blob before pinning it. The blob then behaves like
        /// any runtime <c>Alloc*</c> one (resident while referenced, forgotten once fully released and
        /// evicted). <b>Callers must guard with <see cref="IsResident"/></b> — this throws on a
        /// duplicate id (it is not a no-op).
        /// </summary>
        internal void InsertEagerBlob(BlobId id, object blob)
        {
            AssertMainThreadAndNotDisposed();
            AddEagerBlob(id, blob);
        }

        void AddEagerBlob(BlobId id, object blob)
        {
            Type metadataType;
            long nativeBytes;
            bool isNative;

            // The stored object's runtime type is the discriminant: a native blob is a
            // NativeBlobBox (and carries its own size + inner type), anything else is managed. An
            // eager blob's type id is its concrete type — there's no source declaring a base type.
            if (blob is NativeBlobBox box)
            {
                metadataType = box.InnerType;
                nativeBytes = box.Size;
                isNative = true;
            }
            else
            {
                metadataType = blob.GetType();
                TrecsDebugAssert.That(metadataType.IsClass);
                nativeBytes = 0;
                isNative = false;
            }

            InsertResident(
                id,
                blob,
                TypeId.FromType(metadataType),
                isNative,
                isEager: true,
                nativeBytes
            );
        }

        // Shared resident-insert: the Add throws on a duplicate id, so an id collision surfaces
        // before any further bookkeeping — keeping a caller's box-disposing catch block from leaving
        // a freed box resident in the cache. A blob resident-while-inactive counts toward the
        // inactive totals; the Alloc*/Insert callers that immediately CreateHandle for the same id
        // have their 0→1 transition subtract it straight back out (net zero while active, re-counted
        // once disposed).
        void InsertResident(
            BlobId id,
            object blob,
            TypeId typeId,
            bool isNative,
            bool isEager,
            long nativeBytes
        )
        {
            TrecsDebugAssert.That(
                !_resident.ContainsKey(id),
                "A blob already exists under id {0}",
                id
            );

#if DEBUG
            VerifyNoIdCollision(id, blob, typeId, isNative, nativeBytes);
#endif

            _resident.Add(
                id,
                new ResidentEntry
                {
                    Blob = blob,
                    Meta = new BlobMetadata
                    {
                        NativeBytes = nativeBytes,
                        IsNative = isNative,
                        TypeId = typeId,
                        IsEager = isEager,
                    },
                    LruPrev = BlobId.Null,
                    LruNext = BlobId.Null,
                }
            );

            if (isEager)
            {
                _numEagerResident++;
            }

            if (!_blobRefCounts.ContainsKey(id))
            {
                AddToInactiveTotals(id, isNative, nativeBytes);
            }
        }

#if DEBUG
        // Records each id's content fingerprint on first sight and asserts it never changes. See
        // _idFingerprints. Persistent across eviction, so a later re-insert under the same id with
        // different content — explicit-id reuse, or an impure builder re-materializing divergent
        // bytes — trips the assert instead of silently aliasing/desyncing.
        void VerifyNoIdCollision(
            BlobId id,
            object blob,
            TypeId typeId,
            bool isNative,
            long nativeBytes
        )
        {
            var fingerprint = new IdFingerprint(
                typeId,
                isNative,
                nativeBytes,
                ComputeDebugContentHash(blob)
            );
            if (_idFingerprints.TryGetValue(id, out var existing))
            {
                TrecsDebugAssert.That(
                    existing.StructureEquals(in fingerprint),
                    "BlobId collision: id {0} was previously made resident as a {1} blob but is now "
                        + "being inserted as a {2} blob. Content-addressed identity is violated — "
                        + "either a non-content-derived (e.g. hand-picked) id was reused for "
                        + "different content, or two distinct blobs hashed to the same id.",
                    id,
                    existing,
                    fingerprint
                );
                // Content attestation: identical bytes are required whenever both inserts were
                // hashable. A mismatch with matching structure is almost always an impure builder
                // — a descriptor builder or Register factory that read mutable state (time, RNG,
                // assets, world state) — re-materializing after eviction. That divergence is
                // invisible to snapshot checksums (derivable blob ids hash the descriptor, not the
                // content), so this assert is the only thing standing between it and a silent
                // desync.
                TrecsDebugAssert.That(
                    existing.ContentHash == 0
                        || fingerprint.ContentHash == 0
                        || existing.ContentHash == fingerprint.ContentHash,
                    "Blob content divergence: id {0} was previously resident with content hash "
                        + "{1:X16} but is being re-inserted with content hash {2:X16}. The bytes "
                        + "under an id must be identical for the world's lifetime. Likely causes: "
                        + "an impure builder (it must be a pure function of its descriptor/captured "
                        + "inputs — no time, RNG, mutable world state, or per-machine data), or a "
                        + "hand-picked id reused for different content.",
                    id,
                    existing.ContentHash,
                    fingerprint.ContentHash
                );
                // First hashable sighting after an unhashed one: upgrade the record so later
                // inserts attest against it.
                if (existing.ContentHash == 0 && fingerprint.ContentHash != 0)
                {
                    _idFingerprints[id] = fingerprint;
                }
            }
            else
            {
                _idFingerprints.Add(id, fingerprint);
            }
        }

        // 64-bit content hash for attestation: native blobs hash their raw bytes; managed blobs go
        // through the WorldBuilder-installed hasher (serializer-backed), or 0 when unhashable.
        unsafe ulong ComputeDebugContentHash(object blob)
        {
            if (blob is NativeBlobBox box)
            {
                var hash = (ulong)
                    BlobIdGenerator
                        .FromBytes(new ReadOnlySpan<byte>(box.Ptr.ToPointer(), box.Size))
                        .Value;
                return hash == 0 ? 1ul : hash;
            }
            return _debugManagedContentHasher?.Invoke(blob) ?? 0;
        }
#endif

        // ─── Typed reads (resident-only) ────────────────────────────────

        internal bool TryGetManagedBlob<T>(BlobId id, out T value)
            where T : class
        {
            if (!TryGetBlobAndMetadata(id, out var valueBase, out var metadata))
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

        internal T GetManagedBlob<T>(BlobId id)
            where T : class
        {
            AssertMainThreadAndNotDisposed();

            var blob = GetBlobAndMetadata(id, out var metadata);

            TrecsDebugAssert.That(!metadata.IsNative);
            TrecsDebugAssert.That(typeof(T).IsAssignableFrom(TypeId.ToType(metadata.TypeId)));

            return (T)blob;
        }

        internal unsafe bool TryGetNativeBlobPtr<T>(BlobId id, out IntPtr ptr)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();

            if (!TryGetBlobAndMetadata(id, out var blob, out var metadata))
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

        internal IntPtr GetNativeBlobPtr(BlobId id, int innerTypeId)
        {
            AssertMainThreadAndNotDisposed();

            _log.Trace("Looking up native blob with inner type id {0}", innerTypeId);

            var blob = GetBlobAndMetadata(id, out var metadata);

            TrecsDebugAssert.That(metadata.TypeId.Value == innerTypeId);
            TrecsDebugAssert.That(metadata.IsNative);

            return ((NativeBlobBox)blob).Ptr;
        }

        // Returns a read-only ref: shared native blobs are immutable once materialized (the cache
        // is not snapshotted with game state, so a post-materialize mutation would desync).
        internal unsafe ref readonly T GetNativeBlobRef<T>(BlobId id)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();

            var blob = GetBlobAndMetadata(id, out var metadata);

            TrecsDebugAssert.That(metadata.TypeId == TypeId<T>.Value);
            TrecsDebugAssert.That(metadata.IsNative);

            return ref UnsafeUtility.AsRef<T>(((NativeBlobBox)blob).Ptr.ToPointer());
        }

        internal object GetBlob(BlobId id)
        {
            return GetBlobAndMetadata(id, out _);
        }

        public BlobMetadata GetBlobMetadata(BlobId id)
        {
            GetBlobAndMetadata(id, out var metadata);
            return metadata;
        }

        internal object GetBlobAndMetadata(BlobId id, out BlobMetadata metadata)
        {
            var result = TryGetBlobAndMetadata(id, out var blob, out metadata);
            TrecsDebugAssert.That(result, "Attempted to get unregistered blob id {0}", id);
            return blob;
        }

        internal bool TryGetBlob(BlobId id, out object blob)
        {
            AssertMainThreadAndNotDisposed();
            return TryGetBlobAndMetadata(id, out blob, out _);
        }

        /// <summary>
        /// Resolves the bytes + metadata for a <i>resident</i> <paramref name="id"/>. Returns false
        /// if the blob is not resident — the cache cannot materialize on its own (that is the
        /// <see cref="BlobFactory"/>'s job, which inserts the bytes here before they are read). Under
        /// the eager-on-acquire invariant every typed read is on a pinned (⇒ resident) blob, so the
        /// false branch is only ever taken by genuine "is it resident" probes.
        /// </summary>
        internal bool TryGetBlobAndMetadata(BlobId id, out object blob, out BlobMetadata metadata)
        {
            AssertMainThreadAndNotDisposed();

            if (_resident.TryGetIndex(id, out var index))
            {
                ref var entry = ref _resident.GetValueAtIndexByRef(index);
                blob = entry.Blob;
                metadata = entry.Meta;
                return true;
            }

            blob = null;
            metadata = default;
            return false;
        }

        // ─── Handle / reference counting ────────────────────────────────

        internal PtrHandle CreateHandle(BlobId blobId)
        {
            AssertMainThreadAndNotDisposed();

            // Eager residency, enforced as a precondition. Pinning a blob requires its bytes to be
            // resident already — the BlobFactory's EnsureResident materializes-and-inserts before
            // pinning, and the eager Alloc* paths insert before pinning — so the invariant "any blob
            // with a live handle is resident" holds across every acquire path (managed/native heaps,
            // frame-scoped input heaps, clone, deserialize). Holding refcount==0 throughout the
            // materialize keeps the re-entrancy guard and the inactive-total bookkeeping below seeing
            // consistent state.
            TrecsDebugAssert.That(
                _resident.ContainsKey(blobId),
                "Cannot pin blob {0}: it is not resident — ensure residency (BlobFactory."
                    + "EnsureResident, or an eager Alloc) before CreateHandle",
                blobId
            );

            var id = new PtrHandle(_handleIdCounter);
            _handleIdCounter += 1;
            _handles.Add(id, blobId);

            // Ref-count bookkeeping. Two roles:
            //   1. Detect the 1→0 transition in DisposeHandle that turns a blob inactive (the
            //      high-water-mark update site) and the 0→1 transition here that turns it active.
            //   2. The dictionary's keyset *is* the active-blob set used by GetAllActiveBlobIds and
            //      the CleanCaches pass.
            if (_blobRefCounts.TryGetIndex(blobId, out var idx))
            {
                _blobRefCounts.GetValueAtIndexByRef(idx) += 1;
            }
            else
            {
                _blobRefCounts.Add(blobId, 1);

                // 0→1: inactive → active. The blob is resident (the factory ensured it before
                // pinning, or it already was) and, being resident-while-inactive, was counted in the
                // inactive totals — by the Insert/AddEagerBlob that made it resident, or by the
                // dispose that last made it inactive. Subtract it back out. Exact, because residency
                // guarantees the byte size is known.
                if (TryGetResidentMeta(blobId, out var metadata))
                {
                    RemoveFromInactiveTotals(blobId, metadata.IsNative, metadata.NativeBytes);
                }
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

                // Last handle released: active → inactive. The eager-on-acquire invariant means the
                // blob is resident with its byte size known, so it joins the inactive totals
                // exactly; then the high-water check decides whether to evict. This is the only site
                // that can grow the inactive totals across a transition, hence the only eviction
                // trigger — acquiring a handle makes a blob active, which can only shrink them.
                if (TryGetResidentMeta(blobId, out var metadata))
                {
                    AddToInactiveTotals(blobId, metadata.IsNative, metadata.NativeBytes);
                }

                MaybeRunInlineEviction();
            }
        }

        internal bool ContainsHandle(PtrHandle handleId)
        {
            return _handles.ContainsKey(handleId);
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
                        TryGetResidentMeta(blobId, out var residentEntry)
                        && seenTypes.Add(residentEntry.TypeId)
                    )
                    {
                        if (typeNames.Length > 0)
                        {
                            typeNames.Append(", ");
                        }

                        typeNames.Append(TypeId.ToType(residentEntry.TypeId).GetPrettyName());
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

            int numDisposed = 0;
            foreach (var (_, entry) in _resident)
            {
                DisposeBlob(entry.Blob);
                numDisposed += 1;
            }
            _resident.Clear();
            _numEagerResident = 0;
            _log.Debug("Disposed {0} resident blobs", numDisposed);

#if DEBUG
            // The collision-guard fingerprint map survives eviction by design, so it only ever
            // grows; drop it on teardown so it doesn't outlive the cache it guards.
            _idFingerprints.Clear();
#endif

            _hasDisposed = true;
        }
    }
}
