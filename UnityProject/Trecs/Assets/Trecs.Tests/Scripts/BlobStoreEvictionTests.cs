using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Unit tests for the blob-cache LRU eviction policy: native byte caps,
    /// managed count caps, active-blob pinning, mixed-kind independence, the
    /// <c>updateAccessTime: false</c> contract, and the
    /// <see cref="BlobStoreCommon.BumpCounterAbove"/> cross-run preservation
    /// path. Also covers the inline high-water-mark eviction trigger introduced
    /// with the periodic-tick removal.
    /// </summary>
    [TestFixture]
    public class BlobStoreEvictionTests
    {
        // ───────────────────────────────────────────────────────────
        // Helpers
        // ───────────────────────────────────────────────────────────

        static BlobStoreInMemory CreateStoreWithCaps(
            float maxInactiveNativeBlobsMb,
            int maxInactiveManagedBlobsCount
        )
        {
            return new BlobStoreInMemory(
                new BlobStoreInMemorySettings
                {
                    MaxInactiveNativeBlobsMb = maxInactiveNativeBlobsMb,
                    MaxInactiveManagedBlobsCount = maxInactiveManagedBlobsCount,
                },
                poolManager: null
            );
        }

        // Bytes -> Mb literal. Avoids tripping over int->float precision for
        // small caps like "exactly two int blobs" (8 bytes).
        static float BytesToMb(long bytes)
        {
            return bytes / (1024f * 1024f);
        }

        static BlobCache CreateBlobCacheWithStore(
            BlobStoreInMemory store,
            float highWaterMarkMultiplier = 1.5f
        )
        {
            return new BlobCache(
                TrecsLog.Default,
                new List<IBlobStore> { store },
                new BlobCacheSettings
                {
                    SerializationVersion = 1,
                    HighWaterMarkMultiplier = highWaterMarkMultiplier,
                },
                new NativeBlobBoxPool()
            );
        }

        static ReadOnlyBlobIdSet EmptyActiveSet()
        {
            // Pass an empty DenseDictionary as the backing — none of the blobs
            // we just inserted are "active" from CleanCache's perspective.
            return new ReadOnlyBlobIdSet(new DenseDictionary<BlobId, int>());
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 1: Native byte cap (oldest-by-LastAccessTime evicted first)
        // ───────────────────────────────────────────────────────────

        [Test]
        public void CleanCache_NativeByteCap_EvictsOldestFirst()
        {
            // Cap = 8 bytes = exactly two int blobs. Five 4-byte native blobs
            // → 20 bytes inactive; eviction must drain to <= 8 bytes (i.e.
            // remove at least 3 of the 5). LRU order is allocation order
            // here, so blobs 1..3 should be the ones evicted.
            using var pool = new NativeBlobBoxPool();
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: BytesToMb(8),
                maxInactiveManagedBlobsCount: 1024
            );

            for (int i = 1; i <= 5; i++)
            {
                var box = pool.RentFromValue<int>(i * 10);
                store.CreateBlobImpl(new BlobId(i), box, isNative: true);
            }

            // Sanity: nothing pinned, so all five are inactive.
            var statsBefore = store.GetStats(EmptyActiveSet());
            NAssert.AreEqual(5 * 4, statsBefore.TotalNativeMemoryBytes);
            NAssert.AreEqual(5 * 4, statsBefore.InactiveNativeMemoryBytes);

            store.CleanCache(EmptyActiveSet());

            var statsAfter = store.GetStats(EmptyActiveSet());
            // After eviction, inactive native bytes must be at or under cap.
            NAssert.LessOrEqual(statsAfter.InactiveNativeMemoryBytes, 8);

            // Specifically, the two newest (BlobId 4 and 5) should survive,
            // and BlobId 1..3 should be gone.
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(1)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(2)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(3)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(4)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(5)));
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 2: Managed count cap (LRU order)
        // ───────────────────────────────────────────────────────────

        [Test]
        public void CleanCache_ManagedCountCap_EvictsOldestFirst()
        {
            // Five managed blobs in, cap = 2. Three oldest should be evicted.
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: 100f,
                maxInactiveManagedBlobsCount: 2
            );

            for (int i = 1; i <= 5; i++)
            {
                store.CreateBlobImpl(
                    new BlobId(i),
                    new List<object> { $"managed-{i}" },
                    isNative: false
                );
            }

            var statsBefore = store.GetStats(EmptyActiveSet());
            NAssert.AreEqual(5, statsBefore.TotalManagedEntries);
            NAssert.AreEqual(5, statsBefore.InactiveManagedEntries);

            store.CleanCache(EmptyActiveSet());

            var statsAfter = store.GetStats(EmptyActiveSet());
            NAssert.AreEqual(2, statsAfter.InactiveManagedEntries);

            // Newest two survive.
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(1)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(2)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(3)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(4)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(5)));
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 3: Active blobs never evicted
        // ───────────────────────────────────────────────────────────

        [Test]
        public void CleanCache_ActiveBlobsNeverEvicted()
        {
            // Cap = 0 (every inactive blob would normally be evicted on the
            // first clean pass). All five blobs are pinned via the
            // active-set, so nothing should disappear.
            using var pool = new NativeBlobBoxPool();
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: 0f,
                maxInactiveManagedBlobsCount: 0
            );

            for (int i = 1; i <= 5; i++)
            {
                var box = pool.RentFromValue<int>(i);
                store.CreateBlobImpl(new BlobId(i), box, isNative: true);
            }
            for (int i = 6; i <= 10; i++)
            {
                store.CreateBlobImpl(new BlobId(i), new List<object> { i }, isNative: false);
            }

            // Build an active-set that pins every entry. ReadOnlyBlobIdSet is
            // backed by a DenseDictionary<BlobId, int>, so we mirror what
            // BlobCache itself does: synthesize a ref-count entry per id.
            var activeRefCounts = new DenseDictionary<BlobId, int>();
            for (int i = 1; i <= 10; i++)
            {
                activeRefCounts.Add(new BlobId(i), 1);
            }
            var activeSet = new ReadOnlyBlobIdSet(activeRefCounts);

            store.CleanCache(activeSet);

            // All ten survive — the pinning contract: eviction cannot pull
            // bytes out from under a live handle.
            for (int i = 1; i <= 10; i++)
            {
                NAssert.IsTrue(
                    store.MemoryCache.ContainsKey(new BlobId(i)),
                    $"BlobId({i}) was evicted despite being active"
                );
            }
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 4: Mixed native+managed - independent caps
        // ───────────────────────────────────────────────────────────

        [Test]
        public void CleanCache_Mixed_ManagedUnderCap_NativeOverCap_OnlyNativeEvicted()
        {
            // Three native (over the native byte cap), two managed (under
            // the managed count cap). Only natives should be evicted.
            using var pool = new NativeBlobBoxPool();
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: BytesToMb(4),
                maxInactiveManagedBlobsCount: 10
            );

            for (int i = 1; i <= 3; i++)
            {
                var box = pool.RentFromValue<int>(i);
                store.CreateBlobImpl(new BlobId(i), box, isNative: true);
            }
            for (int i = 100; i <= 101; i++)
            {
                store.CreateBlobImpl(new BlobId(i), new List<object> { i }, isNative: false);
            }

            store.CleanCache(EmptyActiveSet());

            // Native count: started at 3 (12 bytes), cap is 4 bytes -> exactly
            // one native should survive (the newest, BlobId(3)).
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(1)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(2)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(3)));

            // Managed: under cap, both survive.
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(100)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(101)));
        }

        [Test]
        public void CleanCache_Mixed_NativeUnderCap_ManagedOverCap_OnlyManagedEvicted()
        {
            // Mirror image: native under its cap, managed over its cap.
            using var pool = new NativeBlobBoxPool();
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: 100f,
                maxInactiveManagedBlobsCount: 1
            );

            for (int i = 1; i <= 2; i++)
            {
                var box = pool.RentFromValue<int>(i);
                store.CreateBlobImpl(new BlobId(i), box, isNative: true);
            }
            for (int i = 100; i <= 103; i++)
            {
                store.CreateBlobImpl(new BlobId(i), new List<object> { i }, isNative: false);
            }

            store.CleanCache(EmptyActiveSet());

            // Natives: under cap, both survive.
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(1)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(2)));

            // Managed: started at 4, cap is 1 -> only the newest (BlobId 103)
            // survives.
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(100)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(101)));
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(102)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(103)));
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 5: updateAccessTime: false does not bump LRU
        // ───────────────────────────────────────────────────────────

        [Test]
        public void CleanCache_TryGetBlobAndMetadata_UpdateAccessTimeFalse_DoesNotBumpLru()
        {
            // Three managed blobs allocated in order 1, 2, 3 (LRU times
            // increasing). Then we "read" BlobId(1) with updateAccessTime:
            // false — this MUST NOT bump its LRU time. Then we read BlobId(2)
            // with updateAccessTime: true — this bumps 2 to the newest. Cap
            // at 1 inactive entry: BlobId(1) (oldest, since its read was a
            // no-op for LRU) must be evicted first, leaving BlobId(2) (most
            // recently accessed) and BlobId(3) (just-newer-than-1 by alloc
            // time). Cap = 1 — but only one of {2, 3} survives; per LRU
            // order, the older of those (BlobId 3) is the next to fall.
            //
            // Concretely: after the access bump, LRU order is [1 (oldest) ->
            // 3 -> 2 (newest)]. Cap = 1, so two of three must be evicted:
            // BlobId(1) and BlobId(3), leaving BlobId(2).
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: 100f,
                maxInactiveManagedBlobsCount: 1
            );

            for (int i = 1; i <= 3; i++)
            {
                store.CreateBlobImpl(
                    new BlobId(i),
                    new List<object> { $"managed-{i}" },
                    isNative: false
                );
            }

            // The protected contract: an updateAccessTime=false read MUST NOT
            // promote BlobId(1) ahead of BlobId(2)/(3).
            var found1 = store.TryGetBlobAndMetadata(
                new BlobId(1),
                out _,
                out _,
                updateAccessTime: false
            );
            NAssert.IsTrue(found1);

            // The true-flag read on BlobId(2) bumps it to the newest entry.
            var found2 = store.TryGetBlobAndMetadata(
                new BlobId(2),
                out _,
                out _,
                updateAccessTime: true
            );
            NAssert.IsTrue(found2);

            store.CleanCache(EmptyActiveSet());

            // Only BlobId(2) — the most recently accessed — survives.
            NAssert.IsFalse(
                store.MemoryCache.ContainsKey(new BlobId(1)),
                "BlobId(1) should have been evicted as the LRU entry; "
                    + "an updateAccessTime: false read incorrectly bumped its LRU time"
            );
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(3)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(2)));
        }

        [Test]
        public void TryGetManifestEntry_UpdateAccessTimeFalse_DoesNotBumpLru()
        {
            // Same contract via the manifest-only path (no cache hit). After
            // a TryGetManifestEntry(_, false) call on BlobId(1), eviction
            // must still treat BlobId(1) as the oldest.
            using var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: 100f,
                maxInactiveManagedBlobsCount: 1
            );

            for (int i = 1; i <= 2; i++)
            {
                store.CreateBlobImpl(
                    new BlobId(i),
                    new List<object> { $"managed-{i}" },
                    isNative: false
                );
            }

            var found = store.TryGetManifestEntry(new BlobId(1), out _, updateAccessTime: false);
            NAssert.IsTrue(found);

            store.CleanCache(EmptyActiveSet());

            // BlobId(1) is still the oldest; the false-flag read did nothing.
            NAssert.IsFalse(store.MemoryCache.ContainsKey(new BlobId(1)));
            NAssert.IsTrue(store.MemoryCache.ContainsKey(new BlobId(2)));
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 6: BumpCounterAbove / NextAccessTime semantics
        // ───────────────────────────────────────────────────────────

        [Test]
        public void BlobStoreCommon_BumpCounterAbove_BumpsAboveSyntheticMax()
        {
            var common = new BlobStoreCommon(poolManager: null);

            // Initial counter starts at 1, increments per call.
            NAssert.AreEqual(1L, common.NextAccessTime());
            NAssert.AreEqual(2L, common.NextAccessTime());

            // Bump above a synthetic max -> next access returns max + 1.
            common.BumpCounterAbove(100);
            NAssert.AreEqual(101L, common.NextAccessTime());
            NAssert.AreEqual(102L, common.NextAccessTime());
        }

        [Test]
        public void BlobStoreCommon_BumpCounterAbove_LowerValueIsNoOp()
        {
            var common = new BlobStoreCommon(poolManager: null);

            common.BumpCounterAbove(50);
            NAssert.AreEqual(51L, common.NextAccessTime());

            // A subsequent bump to a *lower* value must not roll the
            // counter back — cross-run preservation depends on this so a
            // store whose newest-loaded entry is older than its already-
            // advanced counter doesn't accidentally hand out duplicate
            // access times.
            common.BumpCounterAbove(10);
            NAssert.AreEqual(52L, common.NextAccessTime());
        }

        // ───────────────────────────────────────────────────────────
        // GroupIndex 7: Inline high-water-mark eviction trigger
        // ───────────────────────────────────────────────────────────

        [Test]
        public void BlobCache_InlineEviction_FiresOnHandleDispose_WhenNativeOverHighWater()
        {
            // Wire up a small native cap (8 bytes) and a low high-water
            // multiplier (1.0 so the trigger fires as soon as inactive
            // estimate crosses the cap, not at 1.5x). Five int allocs are
            // 20 inactive bytes once their handles are disposed — that's
            // 20 > 8, so an inline eviction pass must run on the last
            // handle dispose and drain back to <= 8 bytes.
            //
            // The store is owned by the cache once handed off: BlobCache
            // disposes its stores from its own Dispose(), so we don't wrap
            // the store in a `using`.
            var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: BytesToMb(8),
                maxInactiveManagedBlobsCount: 1024
            );
            var cache = CreateBlobCacheWithStore(store, highWaterMarkMultiplier: 1.0f);

            try
            {
                var ptrs = new List<NativeBlobPtr<int>>();
                for (int i = 1; i <= 5; i++)
                {
                    ptrs.Add(cache.AllocNativeBlob<int>(new BlobId(i), i * 10));
                }

                // Before dispose: all five are active, eviction must NOT
                // have fired (any pass would have been a no-op anyway since
                // the active set covers everything, but we additionally
                // assert nothing was touched).
                NAssert.AreEqual(5 * 4, cache.GetStats().TotalNativeMemoryBytes);
                NAssert.AreEqual(0, cache.GetStats().InactiveNativeMemoryBytes);

                // Dispose all five handles. The high-water trigger sits on
                // the 1 -> 0 ref-count transition inside DisposeHandle;
                // when the running inactive estimate crosses the trigger,
                // CleanCaches() runs inline.
                foreach (var ptr in ptrs)
                {
                    ptr.Dispose(cache);
                }

                // Eviction must have fired: inactive total is at or under
                // the configured cap. This is the regression coverage that
                // the alloc/dispose hot-path trigger actually drains.
                var stats = cache.GetStats();
                NAssert.LessOrEqual(stats.InactiveNativeMemoryBytes, 8);
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void BlobCache_InlineEviction_FiresOnHandleDispose_WhenManagedOverHighWater()
        {
            // Same as above but for the managed-count trigger. Store is
            // disposed by the cache, see note in the native variant.
            var store = CreateStoreWithCaps(
                maxInactiveNativeBlobsMb: 100f,
                maxInactiveManagedBlobsCount: 1
            );
            var cache = CreateBlobCacheWithStore(store, highWaterMarkMultiplier: 1.0f);

            try
            {
                var ptrs = new List<BlobPtr<List<object>>>();
                for (int i = 1; i <= 4; i++)
                {
                    ptrs.Add(
                        cache.AllocManagedBlob(new BlobId(i), new List<object> { $"managed-{i}" })
                    );
                }

                NAssert.AreEqual(4, cache.GetStats().TotalManagedEntries);
                NAssert.AreEqual(0, cache.GetStats().InactiveManagedEntries);

                foreach (var ptr in ptrs)
                {
                    ptr.Dispose(cache);
                }

                // Eviction must have fired inline: inactive managed count
                // is at or under cap (1).
                var stats = cache.GetStats();
                NAssert.LessOrEqual(stats.InactiveManagedEntries, 1);
            }
            finally
            {
                cache.Dispose();
            }
        }
    }
}
