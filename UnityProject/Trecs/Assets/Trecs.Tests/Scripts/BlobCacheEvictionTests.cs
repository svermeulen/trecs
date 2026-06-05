using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Unit tests for the <see cref="BlobCache"/> LRU eviction policy: native byte caps,
    /// managed count caps, active-blob pinning, mixed-kind independence, the
    /// pin-cycle-is-use ordering (a re-pin bumps a blob to most-recently-used; a plain read does
    /// not reorder it), the inline high-water-mark trigger, and the register-source
    /// re-materialization path (an evicted registered blob is rebuilt on re-acquire).
    /// <para>
    /// Eager (sourceless) blobs are created with the internal <c>AllocNativeBlob</c> /
    /// <c>AllocManagedBlob</c> + pin-dispose to drive a blob inactive. A deliberately high
    /// <see cref="BlobCacheSettings.HighWaterMarkMultiplier"/> keeps the inline trigger from
    /// firing mid-setup so the explicit <see cref="BlobCache.CleanCaches"/> pass can be observed
    /// in isolation; the dedicated inline-trigger tests use a 1.0 multiplier instead.
    /// </para>
    /// </summary>
    [TestFixture]
    public class BlobCacheEvictionTests
    {
        static BlobCache CreateCache(
            float maxInactiveNativeBlobsMb,
            int maxInactiveManagedBlobsCount,
            float highWaterMarkMultiplier = 1000f
        )
        {
            return new BlobCache(
                TrecsLog.Default,
                new BlobCacheSettings
                {
                    MaxInactiveNativeBlobsMb = maxInactiveNativeBlobsMb,
                    MaxInactiveManagedBlobsCount = maxInactiveManagedBlobsCount,
                    HighWaterMarkMultiplier = highWaterMarkMultiplier,
                },
                new NativeBlobBoxPool()
            );
        }

        // A BlobFactory over the given cache, for the sourced-blob tests (register / acquire /
        // re-materialize). Eager-only tests drive the cache directly.
        static BlobFactory CreateFactory(BlobCache cache)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            return new BlobFactory(TrecsLog.Default, cache, registry);
        }

        // Bytes -> Mb literal. Avoids tripping over int->float precision for small caps like
        // "exactly two int blobs" (8 bytes).
        static float BytesToMb(long bytes) => bytes / (1024f * 1024f);

        // ─── Native byte cap (least-recently-used evicted first) ────────

        [Test]
        public void CleanCaches_NativeByteCap_EvictsOldestFirst()
        {
            // Cap = 8 bytes = exactly two int blobs. Five 4-byte native blobs → 20 bytes inactive
            // once their pins are dropped; eviction drains to <= 8 (removes the 3 oldest).
            using var cache = CreateCache(BytesToMb(8), maxInactiveManagedBlobsCount: 1024);

            for (int i = 1; i <= 5; i++)
            {
                cache.AllocNativeBlob<int>(new BlobId(i), i * 10).Dispose(cache);
            }

            cache.CleanCaches();

            NAssert.IsFalse(cache.IsResident(new BlobId(1)));
            NAssert.IsFalse(cache.IsResident(new BlobId(2)));
            NAssert.IsFalse(cache.IsResident(new BlobId(3)));
            NAssert.IsTrue(cache.IsResident(new BlobId(4)));
            NAssert.IsTrue(cache.IsResident(new BlobId(5)));
            NAssert.LessOrEqual(cache.GetStats().InactiveNativeMemoryBytes, 8);
        }

        // ─── Managed count cap (LRU order) ──────────────────────────────

        [Test]
        public void CleanCaches_ManagedCountCap_EvictsOldestFirst()
        {
            using var cache = CreateCache(100f, maxInactiveManagedBlobsCount: 2);

            for (int i = 1; i <= 5; i++)
            {
                cache
                    .AllocManagedBlob(new BlobId(i), new List<object> { $"managed-{i}" })
                    .Dispose(cache);
            }

            cache.CleanCaches();

            NAssert.IsFalse(cache.IsResident(new BlobId(1)));
            NAssert.IsFalse(cache.IsResident(new BlobId(2)));
            NAssert.IsFalse(cache.IsResident(new BlobId(3)));
            NAssert.IsTrue(cache.IsResident(new BlobId(4)));
            NAssert.IsTrue(cache.IsResident(new BlobId(5)));
            NAssert.AreEqual(2, cache.GetStats().InactiveManagedEntries);
        }

        // ─── Active blobs never evicted ─────────────────────────────────

        [Test]
        public void CleanCaches_ActiveBlobsNeverEvicted()
        {
            // Cap = 0 (every inactive blob would normally be evicted). All ten blobs stay pinned,
            // so nothing disappears: eviction cannot pull bytes out from under a live handle.
            using var cache = CreateCache(0f, maxInactiveManagedBlobsCount: 0);

            var nativePins = new List<NativeSharedAnchor<int>>();
            for (int i = 1; i <= 5; i++)
            {
                nativePins.Add(cache.AllocNativeBlob<int>(new BlobId(i), i));
            }
            var managedPins = new List<SharedAnchor<List<object>>>();
            for (int i = 6; i <= 10; i++)
            {
                managedPins.Add(cache.AllocManagedBlob(new BlobId(i), new List<object> { i }));
            }

            cache.CleanCaches();

            for (int i = 1; i <= 10; i++)
            {
                NAssert.IsTrue(
                    cache.IsResident(new BlobId(i)),
                    $"BlobId({i}) was evicted despite being active"
                );
            }

            foreach (var p in nativePins)
                p.Dispose(cache);
            foreach (var p in managedPins)
                p.Dispose(cache);
        }

        // ─── Mixed native+managed - independent caps ────────────────────

        [Test]
        public void CleanCaches_Mixed_NativeOverCap_OnlyNativeEvicted()
        {
            using var cache = CreateCache(BytesToMb(4), maxInactiveManagedBlobsCount: 10);

            for (int i = 1; i <= 3; i++)
                cache.AllocNativeBlob<int>(new BlobId(i), i).Dispose(cache);
            for (int i = 100; i <= 101; i++)
                cache.AllocManagedBlob(new BlobId(i), new List<object> { i }).Dispose(cache);

            cache.CleanCaches();

            // Native: 3 (12 bytes), cap 4 bytes -> only the newest survives.
            NAssert.IsFalse(cache.IsResident(new BlobId(1)));
            NAssert.IsFalse(cache.IsResident(new BlobId(2)));
            NAssert.IsTrue(cache.IsResident(new BlobId(3)));
            // Managed: under cap, both survive.
            NAssert.IsTrue(cache.IsResident(new BlobId(100)));
            NAssert.IsTrue(cache.IsResident(new BlobId(101)));
        }

        [Test]
        public void CleanCaches_Mixed_ManagedOverCap_OnlyManagedEvicted()
        {
            using var cache = CreateCache(100f, maxInactiveManagedBlobsCount: 1);

            for (int i = 1; i <= 2; i++)
                cache.AllocNativeBlob<int>(new BlobId(i), i).Dispose(cache);
            for (int i = 100; i <= 103; i++)
                cache.AllocManagedBlob(new BlobId(i), new List<object> { i }).Dispose(cache);

            cache.CleanCaches();

            // Native under cap: both survive.
            NAssert.IsTrue(cache.IsResident(new BlobId(1)));
            NAssert.IsTrue(cache.IsResident(new BlobId(2)));
            // Managed: 4 in, cap 1 -> only the newest survives.
            NAssert.IsFalse(cache.IsResident(new BlobId(100)));
            NAssert.IsFalse(cache.IsResident(new BlobId(101)));
            NAssert.IsFalse(cache.IsResident(new BlobId(102)));
            NAssert.IsTrue(cache.IsResident(new BlobId(103)));
        }

        // ─── A pin cycle bumps LRU; a plain read does not ───────────────

        [Test]
        public void CleanCaches_Reacquire_BumpsToMostRecentlyUsed_ReadDoesNot()
        {
            using var cache = CreateCache(100f, maxInactiveManagedBlobsCount: 1);

            for (int i = 1; i <= 3; i++)
                cache
                    .AllocManagedBlob(new BlobId(i), new List<object> { $"managed-{i}" })
                    .Dispose(cache);

            // Inactive LRU order after the three pin-then-dispose cycles (LRU→MRU): 1, 2, 3.

            // A plain read is NOT a "use" — it must not reorder the LRU. Reading the LRU entry
            // BlobId(1) must leave it first in line for eviction.
            cache.GetBlobMetadata(new BlobId(1));

            // A pin cycle (acquire → release) IS the "use" event — it moves BlobId(2) to the
            // most-recently-used end: 1, 3, 2.
            cache.DisposeHandle(cache.CreateHandle(new BlobId(2)));

            cache.CleanCaches();

            // Cap 1 keeps only the most-recently-used inactive blob (BlobId(2)); the read on
            // BlobId(1) did not save it.
            NAssert.IsFalse(
                cache.IsResident(new BlobId(1)),
                "BlobId(1) should have been evicted as the LRU entry; a plain read must not bump LRU"
            );
            NAssert.IsFalse(cache.IsResident(new BlobId(3)));
            NAssert.IsTrue(cache.IsResident(new BlobId(2)));
        }

        // ─── Inline high-water-mark eviction trigger ────────────────────

        [Test]
        public void InlineEviction_FiresOnHandleDispose_WhenNativeOverHighWater()
        {
            // Cap 8 bytes, multiplier 1.0 (trigger fires the instant inactive crosses the cap).
            // Five int allocs are 20 inactive bytes once disposed → an inline pass must drain to
            // <= 8 on the last dispose.
            using var cache = CreateCache(
                BytesToMb(8),
                maxInactiveManagedBlobsCount: 1024,
                highWaterMarkMultiplier: 1.0f
            );

            var ptrs = new List<NativeSharedAnchor<int>>();
            for (int i = 1; i <= 5; i++)
                ptrs.Add(cache.AllocNativeBlob<int>(new BlobId(i), i * 10));

            NAssert.AreEqual(5 * 4, cache.GetStats().TotalNativeMemoryBytes);
            NAssert.AreEqual(0, cache.GetStats().InactiveNativeMemoryBytes);

            foreach (var ptr in ptrs)
                ptr.Dispose(cache);

            NAssert.LessOrEqual(cache.GetStats().InactiveNativeMemoryBytes, 8);
        }

        [Test]
        public void InlineEviction_FiresOnHandleDispose_WhenManagedOverHighWater()
        {
            using var cache = CreateCache(
                100f,
                maxInactiveManagedBlobsCount: 1,
                highWaterMarkMultiplier: 1.0f
            );

            var ptrs = new List<SharedAnchor<List<object>>>();
            for (int i = 1; i <= 4; i++)
                ptrs.Add(
                    cache.AllocManagedBlob(new BlobId(i), new List<object> { $"managed-{i}" })
                );

            NAssert.AreEqual(4, cache.GetStats().TotalManagedEntries);
            NAssert.AreEqual(0, cache.GetStats().InactiveManagedEntries);

            foreach (var ptr in ptrs)
                ptr.Dispose(cache);

            NAssert.LessOrEqual(cache.GetStats().InactiveManagedEntries, 1);
        }

        // ─── Registered-source re-materialization after eviction ────────

        [Test]
        public void RegisteredBlob_IsRematerialized_AfterEviction()
        {
            // A registered (sourced) blob keeps its source on the factory through eviction and is
            // transparently rebuilt on re-acquire — unlike an eager blob, which is forgotten once
            // evicted (its bytes drop and the cache no longer knows it).
            using var cache = CreateCache(
                maxInactiveNativeBlobsMb: 0f,
                maxInactiveManagedBlobsCount: 1024,
                highWaterMarkMultiplier: 1.0f
            );
            var factory = CreateFactory(cache);

            factory.RegisterNativeBlob<int>(new BlobId(1), () => 777);

            var p1 = factory.AcquireNativeSharedAnchor<int>(new BlobId(1));
            NAssert.AreEqual(777, p1.Get(cache));
            p1.Dispose(cache);

            // Cap is 0 inactive native bytes, so the blob's bytes are evicted from memory once its
            // last handle drops.
            cache.CleanCaches();

            // The bytes are gone from the cache, but the source remains on the factory, so the blob
            // rebuilds on re-acquire.
            NAssert.IsFalse(cache.IsResident(new BlobId(1)));
            NAssert.IsTrue(factory.IsRegistered(new BlobId(1)));
            var p2 = factory.AcquireNativeSharedAnchor<int>(new BlobId(1));
            NAssert.AreEqual(777, p2.Get(cache));
            p2.Dispose(cache);
        }

        // ─── Taking-ownership (variable-sized) re-materialization ───────

        [Test]
        public void RegisteredTakingOwnershipBlob_IsRematerialized_AfterEviction()
        {
            // The variable-sized native source path: RegisterNativeBlobTakingOwnership backs the blob
            // with a NativeOwnershipBlobSource<T> whose factory allocates a fresh NativeBlobAllocation
            // per call and hands ownership to the cache. Like the inline-value source it keeps its
            // source on the factory through eviction, so the blob — header struct plus a trailing
            // BlobArray<int> in one contiguous allocation — rebuilds from scratch (a brand-new buffer
            // the cache takes ownership of) on re-acquire. This is the direct (non-descriptor)
            // taking-ownership flavor; the descriptor flavor rides the same source via the interner.
            using var cache = CreateCache(
                maxInactiveNativeBlobsMb: 0f,
                maxInactiveManagedBlobsCount: 1024,
                highWaterMarkMultiplier: 1.0f
            );
            var factory = CreateFactory(cache);

            const int count = 8;
            var id = new BlobId(1);
            factory.RegisterNativeBlobTakingOwnership<VariableSizedBlob>(
                id,
                () => BuildVariableSizedBlob(count)
            );

            var p1 = factory.AcquireNativeSharedAnchor<VariableSizedBlob>(id);
            AssertVariableSizedBlob(p1.Get(cache), count);
            p1.Dispose(cache);

            // Cap 0 inactive native bytes → the owned allocation is freed on the CleanCaches pass.
            cache.CleanCaches();
            NAssert.IsFalse(cache.IsResident(id));
            NAssert.IsTrue(factory.IsRegistered(id));

            // Re-acquire re-runs the factory, allocating a fresh buffer the cache takes ownership of,
            // and the rebuilt blob round-trips identically.
            var p2 = factory.AcquireNativeSharedAnchor<VariableSizedBlob>(id);
            AssertVariableSizedBlob(p2.Get(cache), count);
            p2.Dispose(cache);
        }

        // A header field plus a trailing variable-length BlobArray<int> — the canonical
        // "blob bigger than its inline storage" shape that the taking-ownership path exists for.
        [NonCopyable]
        readonly struct VariableSizedBlob
        {
            public readonly int Header;
            public readonly BlobArray<int> Values;

            public VariableSizedBlob(int header)
            {
                Header = header;
                Values = default;
            }
        }

        const int VariableSizedBlobHeader = 0xBEEF;

        // Lays out the root struct + values in a single contiguous allocation via BlobBuilder and
        // returns the NativeBlobAllocation for the cache to take ownership of. Values[i] == i * 100.
        static NativeBlobAllocation BuildVariableSizedBlob(int count)
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<VariableSizedBlob>();
            root = new VariableSizedBlob(VariableSizedBlobHeader);

            var values = builder.Allocate(in root.Values, count);
            for (int i = 0; i < count; i++)
            {
                values[i] = i * 100;
            }
            return builder.BuildNativeBlobAllocation();
        }

        static void AssertVariableSizedBlob(in VariableSizedBlob blob, int count)
        {
            NAssert.AreEqual(VariableSizedBlobHeader, blob.Header);
            NAssert.AreEqual(count, blob.Values.Length);
            for (int i = 0; i < count; i++)
            {
                NAssert.AreEqual(i * 100, blob.Values[i], $"Mismatch at index {i}");
            }
        }

        // ─── Re-entrant materialization guard ───────────────────────────

        [Test]
        public void Materialize_RecursiveSameId_ThrowsInsteadOfRecursing()
        {
            // A source factory that re-enters the cache to resolve the *same* blob it is still
            // building would otherwise recurse forever (or, if it terminated, double-add to the
            // cache and orphan a rented box). The guard turns it into a clear error.
            using var cache = CreateCache(
                maxInactiveNativeBlobsMb: 100f,
                maxInactiveManagedBlobsCount: 1024
            );
            var factory = CreateFactory(cache);

            var id = new BlobId(1);
            // A builder that re-enters to resolve the *same* id it is still building.
            factory.RegisterManagedBlob<SelfReferencingBlob>(
                id,
                () => SharedAnchor.Acquire<SelfReferencingBlob>(factory, id).Get(cache)
            );

            // Acquire materializes eagerly, so the recursion fires on the first acquire — the
            // builder's nested same-id acquire re-enters the materialize and trips the guard.
            NAssert.Throws<TrecsException>(() =>
                SharedAnchor.Acquire<SelfReferencingBlob>(factory, id)
            );

            // The guard left no partial state behind: the source is still registered, and the
            // in-progress marker was cleared so a second acquire re-throws cleanly rather than
            // getting stuck "already materializing".
            NAssert.IsTrue(factory.IsRegistered(id));
            NAssert.Throws<TrecsException>(() =>
                SharedAnchor.Acquire<SelfReferencingBlob>(factory, id)
            );
        }

        sealed class SelfReferencingBlob { }

#if DEBUG
        // ─── Cross-content collision guard (DEBUG only) ─────────────────

        [Test]
        public void InsertResident_IdReusedForDifferentContent_Throws()
        {
            // Managed cap 0 so the disposed inactive managed blob is evicted (forgotten) on the
            // CleanCaches pass, leaving only its DEBUG content fingerprint behind. Re-allocating the
            // same id as a native blob must trip the cross-content collision guard rather than
            // silently aliasing two different blobs under one id.
            using var cache = CreateCache(BytesToMb(1024), maxInactiveManagedBlobsCount: 0);

            cache.AllocManagedBlob(new BlobId(1), new List<string> { "first" }).Dispose(cache);
            cache.CleanCaches();
            NAssert.IsFalse(cache.IsResident(new BlobId(1)));

            NAssert.Throws<TrecsException>(() => cache.AllocNativeBlob<int>(new BlobId(1), 42));
        }

        [Test]
        public void InsertResident_IdReusedForSameShape_DoesNotThrow()
        {
            // The guard must NOT fire for a legitimate re-insert under the same id with the same
            // fingerprint (e.g. an evicted-then-reallocated blob of identical type/shape). The
            // fingerprint is type + native-ness + size, so same-shape re-inserts pass even if the
            // managed content differs — content-addressing is what prevents same-id-different-content
            // in the first place; this guard is the structural-type-collision net.
            using var cache = CreateCache(BytesToMb(1024), maxInactiveManagedBlobsCount: 0);

            cache.AllocManagedBlob(new BlobId(1), new List<string> { "first" }).Dispose(cache);
            cache.CleanCaches();
            NAssert.IsFalse(cache.IsResident(new BlobId(1)));

            NAssert.DoesNotThrow(() =>
                cache.AllocManagedBlob(new BlobId(1), new List<string> { "second" }).Dispose(cache)
            );
        }
#endif
    }
}
