using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Standalone tests for <see cref="NativeChunkStore"/>: allocation/free roundtrips,
    /// bucket selection, side-table generation, pre-flush vs deferred free paths, huge-alloc
    /// path, page reclamation, and stale-handle detection.
    /// </summary>
    [TestFixture]
    public class NativeChunkStoreTests
    {
        // ─── Basic alloc/resolve ────────────────────────────────

        [Test]
        public void Alloc_ReturnsResolvableHandle_PreFlush()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(64, 8, typeHash: 42);
            NAssert.IsFalse(handle.IsNull);
            NAssert.AreEqual(1, store.NumLiveAllocations);

            var entry = store.ResolveEntry(handle);
            NAssert.AreNotEqual(IntPtr.Zero, entry.Address);
            NAssert.AreEqual(42, entry.TypeHash);
            NAssert.AreEqual(1, entry.InUse);
            NAssert.AreEqual(1, entry.Generation);
        }

        [Test]
        public void Alloc_ReturnsResolvableHandle_PostFlush()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(64, 8, typeHash: 7);
            store.FlushPendingOperations();

            var entry = store.ResolveEntry(handle);
            NAssert.AreNotEqual(IntPtr.Zero, entry.Address);
            NAssert.AreEqual(7, entry.TypeHash);

            // Burst-side resolver should agree.
            var resolverEntry = store.Resolver.ResolveEntry(handle);
            NAssert.AreEqual(entry.Address, resolverEntry.Address);
            NAssert.AreEqual(entry.TypeHash, resolverEntry.TypeHash);
        }

        [Test]
        public void Alloc_WritableMemoryRoundTrips()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(sizeof(int) * 4, 4, typeHash: 0);
            store.FlushPendingOperations();

            var addr = store.ResolveEntry(handle).Address;
            Marshal.WriteInt32(addr, 0 * sizeof(int), 11);
            Marshal.WriteInt32(addr, 1 * sizeof(int), 22);
            Marshal.WriteInt32(addr, 2 * sizeof(int), 33);
            Marshal.WriteInt32(addr, 3 * sizeof(int), 44);

            var readAddr = store.ResolveEntry(handle).Address;
            NAssert.AreEqual(addr, readAddr, "Address must be stable across resolves");
            NAssert.AreEqual(11, Marshal.ReadInt32(readAddr, 0 * sizeof(int)));
            NAssert.AreEqual(22, Marshal.ReadInt32(readAddr, 1 * sizeof(int)));
            NAssert.AreEqual(33, Marshal.ReadInt32(readAddr, 2 * sizeof(int)));
            NAssert.AreEqual(44, Marshal.ReadInt32(readAddr, 3 * sizeof(int)));
        }

        [Test]
        public void AllocateMany_TracksLiveCount()
        {
            using var store = new NativeChunkStore();
            for (int i = 0; i < 100; i++)
            {
                store.Alloc(32, 8, typeHash: i);
            }
            NAssert.AreEqual(100, store.NumLiveAllocations);
        }

        // ─── Free paths ─────────────────────────────────────────

        [Test]
        public void Free_PreFlush_DecrementsLiveCount()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(32, 8, typeHash: 0);
            NAssert.AreEqual(1, store.NumLiveAllocations);

            store.Free(handle);
            NAssert.AreEqual(0, store.NumLiveAllocations);
        }

        [Test]
        public void Free_AfterFlush_Deferred_DecrementsLiveCount()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();

            store.Free(handle);
            NAssert.AreEqual(0, store.NumLiveAllocations);

            // After the next flush, side-table slot is released.
            store.FlushPendingOperations();
            NAssert.Throws<TrecsException>(() => store.ResolveEntry(handle));
        }

        [Test]
        public void Free_PreFlush_HandleNoLongerResolves()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(32, 8, typeHash: 0);
            store.Free(handle);

            NAssert.Throws<TrecsException>(() => store.ResolveEntry(handle));
        }

        // ─── Bucket reuse ───────────────────────────────────────

        [Test]
        public void AllocFreeAlloc_SameSize_ReusesBucketSlot()
        {
            using var store = new NativeChunkStore();

            // Fill enough to get a stable page.
            var first = store.Alloc(64, 8, typeHash: 0);
            store.FlushPendingOperations();
            var entry1 = store.ResolveEntry(first);
            var addr1 = entry1.Address;

            store.Free(first);
            store.FlushPendingOperations();

            // Next 64-byte alloc should reuse the same physical slot.
            var second = store.Alloc(64, 8, typeHash: 0);
            store.FlushPendingOperations();
            var entry2 = store.ResolveEntry(second);

            NAssert.AreEqual(
                addr1,
                entry2.Address,
                "Bucket should have recycled the same physical slot"
            );
            NAssert.AreNotEqual(
                first.Value,
                second.Value,
                "Recycled slot must produce a new handle value"
            );
        }

        [Test]
        public void DifferentSizes_RouteToDifferentBuckets()
        {
            using var store = new NativeChunkStore();

            var small = store.Alloc(16, 8, typeHash: 0);
            var medium = store.Alloc(256, 8, typeHash: 0);
            var large = store.Alloc(4096, 8, typeHash: 0);
            store.FlushPendingOperations();

            var es = store.ResolveEntry(small);
            var em = store.ResolveEntry(medium);
            var el = store.ResolveEntry(large);

            // Each goes to its own bucket → independent pages.
            NAssert.AreNotEqual(es.PageId, em.PageId);
            NAssert.AreNotEqual(em.PageId, el.PageId);
            NAssert.AreNotEqual(es.PageId, el.PageId);
        }

        [Test]
        public void ManyAllocs_SmallBucket_OnlyAllocatesOnePageInitially()
        {
            using var store = new NativeChunkStore();

            // 16B bucket → 4096B page → 256 slots. Two allocs should fit in one page.
            store.Alloc(16, 8, typeHash: 0);
            store.Alloc(16, 8, typeHash: 0);
            NAssert.AreEqual(1, store.NumPages);
        }

        [Test]
        public void OverflowBucket_AllocatesAdditionalPage()
        {
            using var store = new NativeChunkStore();

            // 64KB bucket → page size = 4MB, slots/page = 64. Allocate 65 — forces a second page.
            for (int i = 0; i < 65; i++)
            {
                store.Alloc(65536, 16, typeHash: 0);
            }
            NAssert.AreEqual(2, store.NumPages);
        }

        // ─── Huge allocation path ───────────────────────────────

        [Test]
        public void HugeAlloc_AboveMaxBucket_GetsDedicatedPage()
        {
            using var store = new NativeChunkStore();

            var huge = store.Alloc(200 * 1024, 16, typeHash: 0); // 200 KB > 64 KB max bucket
            store.FlushPendingOperations();

            var entry = store.ResolveEntry(huge);
            NAssert.AreEqual(1, entry.IsHuge, "Huge alloc must be marked IsHuge=1");
            NAssert.AreEqual(0, entry.SlotIndex, "Huge alloc occupies a single-slot page");
            NAssert.AreEqual(1, store.NumPages);
        }

        [Test]
        public void HugeAlloc_FreeReleasesPage()
        {
            using var store = new NativeChunkStore();

            var huge = store.Alloc(200 * 1024, 16, typeHash: 0);
            store.FlushPendingOperations();
            NAssert.AreEqual(1, store.NumPages);

            store.Free(huge);
            store.FlushPendingOperations();
            NAssert.AreEqual(0, store.NumPages);
        }

        [Test]
        public void HugeAlloc_PreFlushFree_OnDrainedRunsBeforePageRelease()
        {
            // Regression for the Free-orders-safety-drain-before-page-release fix. The
            // pre-flush path used to call ReturnSlot (which calls AllocatorManager.Free for
            // huge pages) before EnforceAllBufferJobsHaveCompletedAndRelease ran on the
            // safety handle. Now Free's onDrained callback runs after EnforceAll but
            // before ReturnSlot, so the entry's Address is still valid memory when the
            // callback inspects it.
            using var store = new NativeChunkStore();
            var huge = store.Alloc(200 * 1024, 16, typeHash: 0);

            IntPtr observedAddress = IntPtr.Zero;
            store.Free(
                huge,
                entry =>
                {
                    // Callback runs post-drain, pre-release: the entry's Address must
                    // still point at a live page allocation (chunk store hasn't called
                    // AllocatorManager.Free on it yet).
                    observedAddress = entry.Address;
                }
            );

            NAssert.AreNotEqual(
                IntPtr.Zero,
                observedAddress,
                "onDrained should have run with a valid address"
            );
        }

        [Test]
        public void HugeAlloc_DeferredFree_OnDrainedRunsAtFlush()
        {
            // Same as above but for the deferred (post-flush) path: onDrained should run
            // during FlushPendingOperations, after EnforceAll but before ReturnSlot.
            using var store = new NativeChunkStore();
            var huge = store.Alloc(200 * 1024, 16, typeHash: 0);
            store.FlushPendingOperations(); // Promote to side table → next Free is deferred.

            IntPtr observedAddress = IntPtr.Zero;
            store.Free(huge, entry => observedAddress = entry.Address);

            // Callback hasn't fired yet — it's queued in pending-frees.
            NAssert.AreEqual(IntPtr.Zero, observedAddress);

            store.FlushPendingOperations();
            NAssert.AreNotEqual(
                IntPtr.Zero,
                observedAddress,
                "onDrained should have run at flush time"
            );
        }

        // ─── Generation / stale handle detection ────────────────

        [Test]
        public void StaleHandle_AfterFreeAndReuse_IsRejected()
        {
            using var store = new NativeChunkStore();

            var h1 = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();
            store.Free(h1);
            store.FlushPendingOperations();

            // Second alloc almost certainly reuses h1's bucket slot AND side-table slot,
            // but with a bumped generation. The original h1 must no longer resolve.
            var h2 = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();
            NAssert.AreNotEqual(h1.Value, h2.Value, "Generation bump must change the handle value");
            NAssert.Throws<TrecsException>(() => store.ResolveEntry(h1));
            NAssert.DoesNotThrow(() => store.ResolveEntry(h2));
        }

        [Test]
        public void StaleHandle_PreFlushFreeThenAlloc_NoCollision()
        {
            // Regression for the pre-flush release path: AcquireSideTableSlot materialises
            // the side-table slot eagerly so the pre-flush free can persist the generation
            // bump even though the slot was never promoted to the side table.
            using var store = new NativeChunkStore();

            var h1 = store.Alloc(32, 8, typeHash: 0);
            store.Free(h1); // pre-flush path

            var h2 = store.Alloc(32, 8, typeHash: 0);
            NAssert.AreNotEqual(h1.Value, h2.Value, "Pre-flush reuse must still bump generation");

            store.FlushPendingOperations();
            NAssert.Throws<TrecsException>(() => store.ResolveEntry(h1));
            NAssert.DoesNotThrow(() => store.ResolveEntry(h2));
        }

        [Test]
        public void Generation_WrapsThroughByteRange_SkippingZero()
        {
            using var store = new NativeChunkStore();

            // Cycle the same slot 260 times → forces generation wraparound through 0.
            PtrHandle latest = default;
            for (int i = 0; i < 260; i++)
            {
                latest = store.Alloc(32, 8, typeHash: 0);
                store.FlushPendingOperations();
                store.Free(latest);
                store.FlushPendingOperations();
            }

            var fresh = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();
            var entry = store.ResolveEntry(fresh);
            NAssert.AreNotEqual(0, entry.Generation, "Generation must skip 0 on wrap");
        }

        // ─── Multiple allocations / handle uniqueness ──────────

        [Test]
        public void Concurrent_Alloc_HandlesAreUnique()
        {
            using var store = new NativeChunkStore();
            var handles = new HashSet<uint>();
            for (int i = 0; i < 1000; i++)
            {
                var h = store.Alloc(32, 8, typeHash: i);
                NAssert.IsTrue(handles.Add(h.Value), $"Handle collision at iteration {i}");
            }
        }

        [Test]
        public void NullHandle_DoesNotResolve()
        {
            using var store = new NativeChunkStore();
            NAssert.Throws<TrecsException>(() => store.ResolveEntry(PtrHandle.Null));
        }

        // ─── Side-table reuse ──────────────────────────────────

        [Test]
        public void SideTableSlot_ReusedAfterFreeAndFlush()
        {
            using var store = new NativeChunkStore();
            var h1 = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();

            NativeChunkStoreResolver.DecodeHandle(h1, out var idx1, out _);

            store.Free(h1);
            store.FlushPendingOperations();

            var h2 = store.Alloc(32, 8, typeHash: 0);
            NativeChunkStoreResolver.DecodeHandle(h2, out var idx2, out _);

            NAssert.AreEqual(idx1, idx2, "Side-table index should recycle after flush");
        }

        // ─── Page reclamation ──────────────────────────────────

        [Test]
        public void ReclaimEmptyPages_FreesAllEmptyPages()
        {
            using var store = new NativeChunkStore();

            // Allocate enough 64KB items to span two pages, then free all.
            var handles = new PtrHandle[65];
            for (int i = 0; i < handles.Length; i++)
            {
                handles[i] = store.Alloc(65536, 16, typeHash: 0);
            }
            store.FlushPendingOperations();
            NAssert.AreEqual(2, store.NumPages);

            for (int i = 0; i < handles.Length; i++)
            {
                store.Free(handles[i]);
            }
            store.FlushPendingOperations();

            var reclaimed = store.ReclaimEmptyPages();
            NAssert.AreEqual(2, reclaimed);
            NAssert.AreEqual(0, store.NumPages);
        }

        [Test]
        public void ReclaimEmptyPages_DoesNotTouchOccupiedPages()
        {
            using var store = new NativeChunkStore();

            // Allocate two pages worth of 64KB slots, free only the second page.
            var handles = new PtrHandle[65];
            for (int i = 0; i < handles.Length; i++)
            {
                handles[i] = store.Alloc(65536, 16, typeHash: 0);
            }
            store.FlushPendingOperations();

            // The 65th alloc forced a new page; freeing exactly it should reclaim only that page.
            store.Free(handles[64]);
            store.FlushPendingOperations();

            var reclaimed = store.ReclaimEmptyPages();
            NAssert.AreEqual(1, reclaimed);
            NAssert.AreEqual(1, store.NumPages);
        }

        [Test]
        public void ReclaimEmptyPages_PageIdsAreRecycled()
        {
            using var store = new NativeChunkStore();
            var h = store.Alloc(65536, 16, typeHash: 0);
            store.FlushPendingOperations();
            var firstPageId = store.ResolveEntry(h).PageId;
            store.Free(h);
            store.FlushPendingOperations();
            store.ReclaimEmptyPages();

            var h2 = store.Alloc(65536, 16, typeHash: 0);
            store.FlushPendingOperations();
            var secondPageId = store.ResolveEntry(h2).PageId;
            NAssert.AreEqual(
                firstPageId,
                secondPageId,
                "Page ID should be recycled after reclamation"
            );
        }

        // ─── Alignment ─────────────────────────────────────────

        [Test]
        public void Alloc_RespectsAlignment()
        {
            using var store = new NativeChunkStore();
            for (int align = 16; align <= 1024; align *= 2)
            {
                var h = store.Alloc(8, align, typeHash: 0);
                store.FlushPendingOperations();
                var addr = store.ResolveEntry(h).Address.ToInt64();
                NAssert.AreEqual(
                    0,
                    addr & (align - 1),
                    $"Address 0x{addr:X} not aligned to {align}"
                );
            }
        }

        // ─── Type-hash storage ─────────────────────────────────

        [Test]
        public void TypeHash_RoundTrips()
        {
            using var store = new NativeChunkStore();
            var h = store.Alloc(32, 8, typeHash: 123456789);
            store.FlushPendingOperations();
            NAssert.AreEqual(123456789, store.ResolveEntry(h).TypeHash);
        }

        // ─── Burst-side resolver ───────────────────────────────

        [BurstCompile]
        struct ResolveJob : IJob
        {
            [ReadOnly]
            public NativeChunkStoreResolver Resolver;
            public PtrHandle Handle;
            public NativeReference<long> AddressSink;
            public NativeReference<int> TypeHashSink;
            public NativeReference<byte> GenerationSink;

            public void Execute()
            {
                var entry = Resolver.ResolveEntry(Handle);
                AddressSink.Value = entry.Address.ToInt64();
                TypeHashSink.Value = entry.TypeHash;
                GenerationSink.Value = entry.Generation;
            }
        }

        [Test]
        public void Resolver_WorksFromBurstJob()
        {
            using var store = new NativeChunkStore();
            var h = store.Alloc(64, 8, typeHash: 999);
            store.FlushPendingOperations();

            var expectedAddress = store.ResolveEntry(h).Address.ToInt64();

            using var addrSink = new NativeReference<long>(Allocator.TempJob);
            using var hashSink = new NativeReference<int>(Allocator.TempJob);
            using var genSink = new NativeReference<byte>(Allocator.TempJob);

            new ResolveJob
            {
                Resolver = store.Resolver,
                Handle = h,
                AddressSink = addrSink,
                TypeHashSink = hashSink,
                GenerationSink = genSink,
            }
                .Schedule()
                .Complete();

            NAssert.AreEqual(expectedAddress, addrSink.Value);
            NAssert.AreEqual(999, hashSink.Value);
            NAssert.AreEqual(1, genSink.Value);
        }

        // ─── Restore (AllocAtSlot / OnDeserializeComplete) ──────

        [Test]
        public void AllocAtSlot_RestoresExactHandleValue()
        {
            using var store = new NativeChunkStore();

            // Synthesize a "saved" handle: encode (gen=7, idx=42).
            var savedHandleValue = NativeChunkStoreResolver.EncodeHandleValue(7, 42);

            var handle = store.AllocAtSlot(savedHandleValue, 32, 8, typeHash: 123);
            NAssert.AreEqual(
                savedHandleValue,
                handle.Value,
                "Restored handle must preserve saved value"
            );

            var entry = store.ResolveEntry(handle);
            NAssert.AreEqual(123, entry.TypeHash);
            NAssert.AreEqual(7, entry.Generation);
            NAssert.AreEqual(1, entry.InUse);
        }

        [Test]
        public void AllocAtSlot_TwoEntries_RoundTrip()
        {
            // Imitate the heap-level save/load: snapshot the handle values, dispose the store,
            // create a fresh one, restore at the saved handle values. The new handles must
            // resolve to fresh storage that's still reachable through those exact handle values.
            uint h1Value,
                h2Value;
            {
                using var src = new NativeChunkStore();
                var h1 = src.Alloc(64, 8, typeHash: 111);
                var h2 = src.Alloc(64, 8, typeHash: 222);
                src.FlushPendingOperations();
                h1Value = h1.Value;
                h2Value = h2.Value;
                NAssert.AreNotEqual(h1Value, h2Value);
            }

            using var dst = new NativeChunkStore();
            var r1 = dst.AllocAtSlot(h1Value, 64, 8, typeHash: 111);
            var r2 = dst.AllocAtSlot(h2Value, 64, 8, typeHash: 222);
            dst.OnDeserializeComplete();

            NAssert.AreEqual(h1Value, r1.Value);
            NAssert.AreEqual(h2Value, r2.Value);
            NAssert.AreEqual(111, dst.ResolveEntry(r1).TypeHash);
            NAssert.AreEqual(222, dst.ResolveEntry(r2).TypeHash);
        }

        [Test]
        public void AllocAtSlot_PreservesData_AfterBlitCopy()
        {
            // Saved + restored slots get fresh memory; the heap layer is responsible for
            // copying contents. Verify the restored slot is at least writable and round-trips
            // a value, since the save/load contract is "the heap blits the data into the
            // restored slot's address."
            using var store = new NativeChunkStore();
            var savedHandleValue = NativeChunkStoreResolver.EncodeHandleValue(3, 10);
            var handle = store.AllocAtSlot(savedHandleValue, sizeof(int), 4, typeHash: 0);
            store.OnDeserializeComplete();

            var addr = store.ResolveEntry(handle).Address;
            Marshal.WriteInt32(addr, 12345);
            NAssert.AreEqual(12345, Marshal.ReadInt32(addr));
        }

        [Test]
        public void OnDeserializeComplete_PreventsCollisionWithFreshAllocs()
        {
            // Without OnDeserializeComplete, _nextFreshSideTableSlot would still be 1 even
            // after restoring slots 10 and 20, and the next normal Alloc would collide.
            using var store = new NativeChunkStore();
            store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(1, 10), 32, 8, 0);
            store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(1, 20), 32, 8, 0);
            store.OnDeserializeComplete();

            // Subsequent fresh Allocs must avoid slots 10 and 20.
            var fresh = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();
            NativeChunkStoreResolver.DecodeHandle(fresh, out var idx, out _);
            NAssert.AreNotEqual(10u, idx);
            NAssert.AreNotEqual(20u, idx);
        }

        [Test]
        public void OnDeserializeComplete_FreeSlotsAreRecycled()
        {
            // OnDeserializeComplete should expose unused slots in [1, maxRestoredSlot] to the
            // free-slot pool so they're reused on subsequent allocs rather than leaked.
            using var store = new NativeChunkStore();
            store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(1, 5), 32, 8, 0);
            store.OnDeserializeComplete();

            // Slots 1..4 are unused; one of them should come back from the next fresh Alloc.
            var fresh = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();
            NativeChunkStoreResolver.DecodeHandle(fresh, out var idx, out _);
            NAssert.LessOrEqual(
                idx,
                5u,
                "Expected fresh alloc to reuse a slot below the restored max"
            );
        }

        [Test]
        public void AcquireSideTableSlot_SkipsStaleFreeListEntries()
        {
            // Regression for B3 — between heap A's OnDeserializeComplete and heap B's
            // AllocAtSlot, _freeSideTableSlots can hold indices that heap B then claims.
            // The next plain Alloc must skip those stale entries and pick a genuinely
            // free slot. The fix lives in AcquireSideTableSlot's pop-and-filter loop.
            using var store = new NativeChunkStore();

            // Imitate heap A's restore: a single entry at slot 10.
            store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(1, 10), 32, 8, 0);
            store.OnDeserializeComplete();
            // Free list now contains slots 1..9 (all currently InUse=0).

            // Imitate heap B's restore claiming slot 5 — the slot is now InUse=1 but
            // still on the free-list stack (OnDeserializeComplete hasn't been called
            // again to rebuild it).
            store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(1, 5), 32, 8, 0);

            // A plain Alloc must NOT hand out slot 5. The stale free-list entry should
            // be popped and discarded, with the next genuinely-free slot returned instead.
            var fresh = store.Alloc(32, 8, typeHash: 0);
            store.FlushPendingOperations();
            NativeChunkStoreResolver.DecodeHandle(fresh, out var idx, out _);
            NAssert.AreNotEqual(
                5u,
                idx,
                "Plain Alloc must not collide with the AllocAtSlot-claimed slot 5"
            );
        }

        // ─── B5: double-register external pointer detection ───

        [Test]
        public void AllocExternal_DoubleRegister_SamePointer_Throws()
        {
            using var store = new NativeChunkStore();

            // Hand-roll a "external" pointer via AllocatorManager so we can register it.
            unsafe
            {
                var size = 32;
                var alignment = 8;
                var raw = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1);
                var addr = new IntPtr(raw);
                try
                {
                    store.AllocExternal(addr, size, alignment, typeHash: 0);

                    // Second AllocExternal with the same pointer must fail loudly.
                    NAssert.Throws<TrecsException>(() =>
                    {
                        store.AllocExternal(addr, size, alignment, typeHash: 0);
                    });
                }
                finally
                {
                    // The first AllocExternal owns the pointer now — store.Dispose will free
                    // it. No need to call AllocatorManager.Free ourselves.
                }
            }
        }

        [Test]
        public void AllocExternal_ReRegisterAfterFree_Succeeds()
        {
            using var store = new NativeChunkStore();

            unsafe
            {
                var size = 32;
                var alignment = 8;
                var raw = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1);
                var addr = new IntPtr(raw);

                var h1 = store.AllocExternal(addr, size, alignment, typeHash: 0);
                store.FlushPendingOperations();
                store.Free(h1);
                store.FlushPendingOperations();
                // Chunk store has called AllocatorManager.Free on addr by now; in real use
                // the address would be invalid. For this test we don't dereference — we just
                // verify the tracking set correctly drops the entry and a re-register works.

                var raw2 = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1);
                var addr2 = new IntPtr(raw2);
                NAssert.DoesNotThrow(() =>
                {
                    store.AllocExternal(addr2, size, alignment, typeHash: 0);
                });
            }
        }

        // ─── B9: Alloc overload returning address ──────────────

        [Test]
        public void Alloc_AddressOutOverload_MatchesResolveEntry()
        {
            using var store = new NativeChunkStore();
            var handle = store.Alloc(64, 8, typeHash: 0, out var address);
            NAssert.AreEqual(address, store.ResolveEntry(handle).Address);
        }

        [Test]
        public void AllocImmediate_AddressOutOverload_MatchesResolveEntry()
        {
            using var store = new NativeChunkStore();
            var handle = store.AllocImmediate(64, 8, typeHash: 0, out var address);
            NAssert.AreEqual(address, store.ResolveEntry(handle).Address);
        }

        [Test]
        public void AllocAtSlot_AddressOutOverload_MatchesResolveEntry()
        {
            using var store = new NativeChunkStore();
            var handle = store.AllocAtSlot(
                NativeChunkStoreResolver.EncodeHandleValue(1, 7),
                64,
                8,
                typeHash: 0,
                out var address
            );
            NAssert.AreEqual(address, store.ResolveEntry(handle).Address);
        }

        [Test]
        public void AllocAtSlot_OnInUseSlot_Throws()
        {
            using var store = new NativeChunkStore();
            var h1 = store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(2, 7), 32, 8, 0);
            // Same slot index, different generation — should still reject (slot is in use).
            NAssert.Throws<TrecsException>(() =>
            {
                store.AllocAtSlot(NativeChunkStoreResolver.EncodeHandleValue(5, 7), 32, 8, 0);
            });
            _ = h1;
        }

        [Test]
        public void Resolver_FromBurstJob_AfterReuseSeesNewGeneration()
        {
            using var store = new NativeChunkStore();

            var h1 = store.Alloc(64, 8, typeHash: 111);
            store.FlushPendingOperations();
            store.Free(h1);
            store.FlushPendingOperations();

            var h2 = store.Alloc(64, 8, typeHash: 222);
            store.FlushPendingOperations();

            using var addrSink = new NativeReference<long>(Allocator.TempJob);
            using var hashSink = new NativeReference<int>(Allocator.TempJob);
            using var genSink = new NativeReference<byte>(Allocator.TempJob);

            new ResolveJob
            {
                Resolver = store.Resolver,
                Handle = h2,
                AddressSink = addrSink,
                TypeHashSink = hashSink,
                GenerationSink = genSink,
            }
                .Schedule()
                .Complete();

            NAssert.AreEqual(222, hashSink.Value);
            NAssert.GreaterOrEqual(
                genSink.Value,
                2,
                "Generation should have advanced past the freed h1"
            );
        }
    }
}
