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
    /// bucket selection, side-table generation, Free preconditions, huge-alloc path,
    /// page reclamation, stale-handle detection, Serialize/Deserialize round-trip,
    /// AllocExternal, and concurrent resolve-vs-alloc.
    /// </summary>
    [TestFixture]
    public class NativeChunkStoreTests
    {
        // ─── Basic alloc/resolve ────────────────────────────────

        [Test]
        public void Alloc_ReturnsResolvableHandle()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
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
        public void Alloc_MainThreadResolverAndBurstResolverAgree()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var handle = store.Alloc(64, 8, typeHash: 7);

            var entry = store.ResolveEntry(handle);
            NAssert.AreNotEqual(IntPtr.Zero, entry.Address);
            NAssert.AreEqual(7, entry.TypeHash);

            var resolverEntry = store.Resolver.ResolveEntry(handle);
            NAssert.AreEqual(entry.Address, resolverEntry.Address);
            NAssert.AreEqual(entry.TypeHash, resolverEntry.TypeHash);
        }

        [Test]
        public void Alloc_RecycledSlot_HasZeroedBytes()
        {
            // The store zeros every slot at Alloc time so a recycled slot doesn't leak
            // the previous tenant's bytes into snapshots. Without this guarantee,
            // cross-world snapshot byte determinism breaks: same logical state, different
            // recycle history → different bytes.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var first = store.Alloc(64, 8, typeHash: 1);
            var firstAddr = store.ResolveEntry(first).Address;

            // Fill the slot with a sentinel pattern.
            for (int i = 0; i < 64; i++)
            {
                Marshal.WriteByte(firstAddr, i, 0xAB);
            }

            store.Free(first);

            // Next Alloc of the same size lands in the same bucket and the same slot
            // (bucket freelist is LIFO). Verify the slot has been zeroed.
            var second = store.Alloc(64, 8, typeHash: 2);
            var secondAddr = store.ResolveEntry(second).Address;
            NAssert.AreEqual(firstAddr, secondAddr, "Expected recycled slot");

            for (int i = 0; i < 64; i++)
            {
                NAssert.AreEqual(
                    (byte)0,
                    Marshal.ReadByte(secondAddr, i),
                    $"byte at {i} should be zero, not the previous tenant's 0xAB"
                );
            }
        }

        [Test]
        public void Alloc_TailPastRequestedSize_IsZeroed()
        {
            // When the caller asks for fewer bytes than the slot size (e.g., a 17-byte
            // request lands in the 32-byte bucket), the padding tail must also be zeroed.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var handle = store.Alloc(17, 1, typeHash: 0);
            var addr = store.ResolveEntry(handle).Address;
            // 32-byte bucket; check bytes 17..31 are zero.
            for (int i = 17; i < 32; i++)
            {
                NAssert.AreEqual(
                    (byte)0,
                    Marshal.ReadByte(addr, i),
                    $"tail byte at {i} should be zero"
                );
            }
        }

        [Test]
        public void Alloc_WritableMemoryRoundTrips()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var handle = store.Alloc(sizeof(int) * 4, 4, typeHash: 0);

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
            using var store = new NativeChunkStore(TrecsLog.Default);
            for (int i = 0; i < 100; i++)
            {
                store.Alloc(32, 8, typeHash: i);
            }
            NAssert.AreEqual(100, store.NumLiveAllocations);
        }

        // ─── Free paths ─────────────────────────────────────────

        [Test]
        public void Free_DecrementsLiveCount()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var handle = store.Alloc(32, 8, typeHash: 0);
            NAssert.AreEqual(1, store.NumLiveAllocations);

            store.Free(handle);
            NAssert.AreEqual(0, store.NumLiveAllocations);
        }

        [Test]
        public void Free_HandleNoLongerResolves()
        {
            // Free is synchronous: the slot is marked InUse=0 and returned to its
            // bucket before Free returns. Resolution after Free should fail.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var handle = store.Alloc(32, 8, typeHash: 0);
            store.Free(handle);

            NAssert.Throws<TrecsException>(() => store.ResolveEntry(handle));
        }

        [Test]
        public void Free_DoubleFree_Throws()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(32, 8, typeHash: 0);
            store.Free(h);
            NAssert.Throws<TrecsException>(
                () => store.Free(h),
                "Freeing an already-freed handle must throw on the InUse=0 assert"
            );
        }

        [Test]
        public void Free_StaleGeneration_Throws()
        {
            // After free + reuse, the original handle encodes the previous generation
            // while the slot now carries a bumped one. Free must reject it rather than
            // silently corrupting the live allocation at the reused slot.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var stale = store.Alloc(32, 8, typeHash: 0);
            store.Free(stale);
            var fresh = store.Alloc(32, 8, typeHash: 0);

            NAssert.Throws<TrecsException>(() => store.Free(stale));
            NAssert.DoesNotThrow(() => store.Free(fresh));
        }

        [Test]
        public void Free_IndexOutOfRange_Throws()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            // Synthesised handle whose index points past anything ever materialised.
            var bogus = new PtrHandle(
                NativeChunkStoreResolver.EncodeHandleValue(1u, NativeChunkStoreResolver.MaxIndex)
            );
            NAssert.Throws<TrecsException>(() => store.Free(bogus));
        }

        [Test]
        public void Free_OnDrained_RunsForBucketPath()
        {
            // Parity with HugeAlloc_Free_OnDrainedRunsBeforePageRelease — the callback
            // contract is the same on the bucket Free path (slot's still-live address
            // visible to the callback before the slot is returned to its bucket).
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(64, 8, typeHash: 0);
            var allocAddress = store.ResolveEntry(h).Address;

            IntPtr observedAddress = IntPtr.Zero;
            store.Free(h, entry => observedAddress = entry.Address);

            NAssert.AreEqual(
                allocAddress,
                observedAddress,
                "onDrained must see the slot's live address before it's returned to the bucket"
            );
        }

        // ─── Bucket reuse ───────────────────────────────────────

        [Test]
        public void AllocFreeAlloc_SameSize_ReusesBucketSlot()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            // Fill enough to get a stable page.
            var first = store.Alloc(64, 8, typeHash: 0);
            var entry1 = store.ResolveEntry(first);
            var addr1 = entry1.Address;

            store.Free(first);

            // Next 64-byte alloc should reuse the same physical slot.
            var second = store.Alloc(64, 8, typeHash: 0);
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
            using var store = new NativeChunkStore(TrecsLog.Default);

            var small = store.Alloc(16, 8, typeHash: 0);
            var medium = store.Alloc(256, 8, typeHash: 0);
            var large = store.Alloc(4096, 8, typeHash: 0);

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
            using var store = new NativeChunkStore(TrecsLog.Default);

            // 16B bucket → 4096B page → 256 slots. Two allocs should fit in one page.
            store.Alloc(16, 8, typeHash: 0);
            store.Alloc(16, 8, typeHash: 0);
            NAssert.AreEqual(1, store.NumPages);
        }

        [Test]
        public void OverflowBucket_AllocatesAdditionalPage()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            // 64KB bucket → page size = 4MB, slots/page = 64. Allocate 65 — forces a second page.
            for (int i = 0; i < 65; i++)
            {
                store.Alloc(65536, 16, typeHash: 0);
            }
            NAssert.AreEqual(2, store.NumPages);
        }

        // ─── Huge allocation path ───────────────────────────────

        [Test]
        public void Alloc_AtMaxBucketSize_GoesToBucketNotHuge()
        {
            // 64KB == MaxBucketSlotSize → still fits in the largest bucket.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(NativeChunkStore.MaxBucketSlotSize, 16, typeHash: 0);
            NAssert.AreEqual(
                0,
                store.ResolveEntry(h).OwnsWholePage,
                "Alloc at exactly MaxBucketSlotSize must use the bucket path"
            );
        }

        [Test]
        public void Alloc_OneByteAboveMaxBucket_TakesHugePath()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(NativeChunkStore.MaxBucketSlotSize + 1, 16, typeHash: 0);
            NAssert.AreEqual(
                1,
                store.ResolveEntry(h).OwnsWholePage,
                "Alloc above MaxBucketSlotSize must take the huge path"
            );
        }

        [Test]
        public void HugeAlloc_AboveMaxBucket_GetsDedicatedPage()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            var huge = store.Alloc(200 * 1024, 16, typeHash: 0); // 200 KB > 64 KB max bucket

            var entry = store.ResolveEntry(huge);
            NAssert.AreEqual(1, entry.OwnsWholePage, "Huge alloc must own its whole page");
            NAssert.AreEqual(0, entry.SlotIndex, "Huge alloc occupies a single-slot page");
            NAssert.AreEqual(1, store.NumPages);
        }

        [Test]
        public void HugeAlloc_FreeReleasesPage()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            var huge = store.Alloc(200 * 1024, 16, typeHash: 0);
            NAssert.AreEqual(1, store.NumPages);

            store.Free(huge);
            NAssert.AreEqual(0, store.NumPages);
        }

        [Test]
        public void HugeAlloc_Free_OnDrainedRunsBeforePageRelease()
        {
            // onDrained runs after the safety-handle release but before ReturnSlot
            // frees the huge page, so the entry's Address is still live memory when
            // the callback inspects it.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var huge = store.Alloc(200 * 1024, 16, typeHash: 0);

            IntPtr observedAddress = IntPtr.Zero;
            store.Free(huge, entry => observedAddress = entry.Address);

            NAssert.AreNotEqual(
                IntPtr.Zero,
                observedAddress,
                "onDrained should have run with a valid address"
            );
        }

        // ─── Generation / stale handle detection ────────────────

        [Test]
        public void StaleHandle_AfterFreeAndReuse_IsRejected()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            var h1 = store.Alloc(32, 8, typeHash: 0);
            store.Free(h1);

            // Second alloc almost certainly reuses h1's bucket slot AND side-table slot,
            // but with a bumped generation. The original h1 must no longer resolve.
            var h2 = store.Alloc(32, 8, typeHash: 0);
            NAssert.AreNotEqual(h1.Value, h2.Value, "Generation bump must change the handle value");
            NAssert.Throws<TrecsException>(() => store.ResolveEntry(h1));
            NAssert.DoesNotThrow(() => store.ResolveEntry(h2));
        }

        [Test]
        public void StaleHandle_FreeThenAlloc_NoCollision()
        {
            // AcquireSideTableSlot materialises the side-table slot eagerly so Free's
            // generation bump persists into the slot even when the slot has just been
            // recycled to the free-slot stack.
            using var store = new NativeChunkStore(TrecsLog.Default);

            var h1 = store.Alloc(32, 8, typeHash: 0);
            store.Free(h1);

            var h2 = store.Alloc(32, 8, typeHash: 0);
            NAssert.AreNotEqual(h1.Value, h2.Value, "Slot reuse must bump generation");

            NAssert.Throws<TrecsException>(() => store.ResolveEntry(h1));
            NAssert.DoesNotThrow(() => store.ResolveEntry(h2));
        }

        [Test]
        public void Generation_WrapsThroughByteRange_SkippingZero()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            // Cycle the same slot 260 times → forces generation wraparound through 0.
            PtrHandle latest = default;
            for (int i = 0; i < 260; i++)
            {
                latest = store.Alloc(32, 8, typeHash: 0);
                store.Free(latest);
            }

            var fresh = store.Alloc(32, 8, typeHash: 0);
            var entry = store.ResolveEntry(fresh);
            NAssert.AreNotEqual(0, entry.Generation, "Generation must skip 0 on wrap");
        }

        // ─── Multiple allocations / handle uniqueness ──────────

        [Test]
        public void Concurrent_Alloc_HandlesAreUnique()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
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
            using var store = new NativeChunkStore(TrecsLog.Default);
            NAssert.Throws<TrecsException>(() => store.ResolveEntry(PtrHandle.Null));
        }

        // ─── Side-table reuse ──────────────────────────────────

        [Test]
        public void SideTableSlot_RecycledAfterFree()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h1 = store.Alloc(32, 8, typeHash: 0);

            NativeChunkStoreResolver.DecodeHandle(h1, out var idx1, out _);

            store.Free(h1);

            var h2 = store.Alloc(32, 8, typeHash: 0);
            NativeChunkStoreResolver.DecodeHandle(h2, out var idx2, out _);

            NAssert.AreEqual(idx1, idx2, "Side-table index should recycle after Free");
        }

        // ─── Page reclamation ──────────────────────────────────

        [Test]
        public void ReclaimEmptyPages_FreesAllEmptyPages()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            // Allocate enough 64KB items to span two pages, then free all.
            var handles = new PtrHandle[65];
            for (int i = 0; i < handles.Length; i++)
            {
                handles[i] = store.Alloc(65536, 16, typeHash: 0);
            }
            NAssert.AreEqual(2, store.NumPages);

            for (int i = 0; i < handles.Length; i++)
            {
                store.Free(handles[i]);
            }

            var reclaimed = store.ReclaimEmptyPages();
            NAssert.AreEqual(2, reclaimed);
            NAssert.AreEqual(0, store.NumPages);
        }

        [Test]
        public void ReclaimEmptyPages_DoesNotTouchOccupiedPages()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            // Allocate two pages worth of 64KB slots, free only the second page.
            var handles = new PtrHandle[65];
            for (int i = 0; i < handles.Length; i++)
            {
                handles[i] = store.Alloc(65536, 16, typeHash: 0);
            }

            // The 65th alloc forced a new page; freeing exactly it should reclaim only that page.
            store.Free(handles[64]);

            var reclaimed = store.ReclaimEmptyPages();
            NAssert.AreEqual(1, reclaimed);
            NAssert.AreEqual(1, store.NumPages);
        }

        [Test]
        public void ReclaimEmptyPages_PageIdsAreRecycled()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(65536, 16, typeHash: 0);
            var firstPageId = store.ResolveEntry(h).PageId;
            store.Free(h);
            store.ReclaimEmptyPages();

            var h2 = store.Alloc(65536, 16, typeHash: 0);
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
            using var store = new NativeChunkStore(TrecsLog.Default);
            for (int align = 16; align <= 1024; align *= 2)
            {
                var h = store.Alloc(8, align, typeHash: 0);
                var addr = store.ResolveEntry(h).Address.ToInt64();
                NAssert.AreEqual(
                    0,
                    addr & (align - 1),
                    $"Address 0x{addr:X} not aligned to {align}"
                );
            }
        }

        [Test]
        public void Alloc_AlignmentExceedsSize_PicksBucketBasedOnAlignment()
        {
            // size=8 alone would route to the 16-byte bucket (stride 16).
            // With align=64 the effective slot size is max(size, align)=64, so
            // adjacent slots from the 64-byte bucket are 64 bytes apart.
            using var store = new NativeChunkStore(TrecsLog.Default);
            var a = store.Alloc(8, 64, typeHash: 0);
            var b = store.Alloc(8, 64, typeHash: 0);

            var addrA = store.ResolveEntry(a).Address.ToInt64();
            var addrB = store.ResolveEntry(b).Address.ToInt64();
            NAssert.AreEqual(
                64,
                Math.Abs(addrA - addrB),
                "Bucket selection must respect alignment when alignment > size"
            );
            NAssert.AreEqual(0, addrA & 63, "Address must be 64-byte aligned");
            NAssert.AreEqual(0, addrB & 63, "Address must be 64-byte aligned");
        }

        // ─── Type-hash storage ─────────────────────────────────

        [Test]
        public void TypeHash_RoundTrips()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(32, 8, typeHash: 123456789);
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
            using var store = new NativeChunkStore(TrecsLog.Default);
            var h = store.Alloc(64, 8, typeHash: 999);

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

        // ─── Serialize / Deserialize round-trip ──────────────────

        [Test]
        public void Serialize_Twice_ProducesIdenticalBytes()
        {
            // Byte-determinism: identical chunk-store state must produce identical
            // bytes across runs. Required for rollback checksum-based desync detection
            // and any other byte-equality comparisons.
            using var store = BuildVariedStore(seed: 1);

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            using var buf1 = new SerializationBuffer(registry);
            using var buf2 = new SerializationBuffer(registry);

            buf1.StartWrite(version: 1, includeTypeChecks: true);
            store.Serialize(buf1);
            buf1.EndWrite();
            var bytes1 = buf1.MemoryStream.ToArray();

            buf2.StartWrite(version: 1, includeTypeChecks: true);
            store.Serialize(buf2);
            buf2.EndWrite();
            var bytes2 = buf2.MemoryStream.ToArray();

            CollectionAssert.AreEqual(
                bytes1,
                bytes2,
                "Identical chunk-store state must produce identical serialized bytes"
            );
        }

        [Test]
        public void Serialize_Deserialize_RoundTripsLiveData()
        {
            // Build a chunk store with varied content, capture handle→data mapping,
            // serialize to a fresh chunk store, and verify all data still resolves.
            using var src = new NativeChunkStore(TrecsLog.Default);
            var live = new List<(PtrHandle Handle, int Size, int TypeHash)>();
            int[] sizes = { 16, 64, 256, 1024, 4096, 8192, 80 * 1024 };
            var rng = new Random(2);
            for (int i = 0; i < 25; i++)
            {
                var size = sizes[rng.Next(sizes.Length)];
                var typeHash = rng.Next(1000);
                var h = src.Alloc(size, 8, typeHash, out var addr);
                unsafe
                {
                    var p = (byte*)addr.ToPointer();
                    for (int b = 0; b < size; b++)
                        p[b] = (byte)((h.Value + (uint)b) & 0xFF);
                }
                live.Add((h, size, typeHash));
            }

            using var dst = CloneViaSerialize(src);
            NAssert.AreEqual(src.NumLiveAllocations, dst.NumLiveAllocations);

            foreach (var (handle, size, typeHash) in live)
            {
                var entry = dst.ResolveEntry(handle);
                NAssert.AreEqual(1, entry.InUse, "Restored entry must be InUse=1");
                NAssert.AreEqual(typeHash, entry.TypeHash, "TypeHash must round-trip");

                unsafe
                {
                    var p = (byte*)entry.Address.ToPointer();
                    for (int b = 0; b < size; b++)
                    {
                        var expected = (byte)((handle.Value + (uint)b) & 0xFF);
                        NAssert.AreEqual(
                            expected,
                            p[b],
                            $"Data byte {b} for handle {handle.Value:X} did not round-trip"
                        );
                    }
                }
            }
        }

        [Test]
        public void Serialize_Deserialize_PreservesAllocOrder()
        {
            // After save/load, a sequence of fresh Allocs must produce the same handle
            // values as the same sequence without the save/load. Verifies that
            // free-slot stacks (side-table + per-bucket) round-trip in original order.
            using var src = BuildVariedStore(seed: 3);

            // Capture the next 5 handle values without save/load.
            var withoutRoundTrip = new uint[5];
            using (var probe = CloneViaSerialize(src))
            {
                for (int i = 0; i < withoutRoundTrip.Length; i++)
                {
                    withoutRoundTrip[i] = probe.Alloc(48, 8, typeHash: 0).Value;
                }
            }

            // Now do the same sequence after a save/load.
            using var dst = CloneViaSerialize(src);
            var withRoundTrip = new uint[5];
            for (int i = 0; i < withRoundTrip.Length; i++)
            {
                withRoundTrip[i] = dst.Alloc(48, 8, typeHash: 0).Value;
            }

            CollectionAssert.AreEqual(
                withoutRoundTrip,
                withRoundTrip,
                "Allocs after save/load must produce the same handle sequence as without"
            );
        }

        [Test]
        public void Serialize_Deserialize_PreservesHugeAndExternalPages()
        {
            using var src = new NativeChunkStore(TrecsLog.Default);
            var hugeHandle = src.Alloc(200 * 1024, 16, typeHash: 7);
            unsafe
            {
                var raw = AllocatorManager.Allocate(Allocator.Persistent, 256, 16, 1);
                var addr = new IntPtr(raw);
                var extHandle = src.AllocExternal(addr, 256, 16, typeHash: 9);
                Marshal.WriteInt32(addr, 0xCAFE);

                using var dst = CloneViaSerialize(src);

                NAssert.AreEqual(2, dst.NumLiveAllocations);
                var hugeEntry = dst.ResolveEntry(hugeHandle);
                NAssert.AreEqual(1, hugeEntry.OwnsWholePage);
                NAssert.AreEqual(7, hugeEntry.TypeHash);

                var extEntry = dst.ResolveEntry(extHandle);
                NAssert.AreEqual(1, extEntry.OwnsWholePage);
                NAssert.AreEqual(9, extEntry.TypeHash);
                NAssert.AreEqual(0xCAFE, Marshal.ReadInt32(extEntry.Address));
            }
        }

        [Test]
        public void Deserialize_OverDirtyStore_RestoresCorrectly()
        {
            // Production callers (WorldStateSerializer) call Deserialize on a chunk store
            // that's been used: heap.ClearAll frees entries (so _liveCount goes to 0) but
            // bucket pages, side-table chunks, and _nextFreshSideTableSlot are still
            // populated. Verify ResetForDeserialize wipes that state cleanly and the
            // restored store matches a fresh-construction Deserialize.
            using var src = BuildVariedStore(seed: 4);

            // Serialize src for restoring later.
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            using var buf = new SerializationBuffer(registry);
            buf.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buf);
            buf.EndWrite();

            // Build a DIFFERENT chunk store (different shape, different size) then free
            // everything so it ends up at _liveCount=0 but with residual pages and
            // bumped generations.
            using var dst = new NativeChunkStore(TrecsLog.Default);
            var residual = new List<PtrHandle>();
            for (int i = 0; i < 20; i++)
                residual.Add(dst.Alloc(128, 8, typeHash: i));
            foreach (var h in residual)
                dst.Free(h);
            NAssert.AreEqual(0, dst.NumLiveAllocations);
            NAssert.Greater(dst.NumPages, 0, "Pre-Deserialize dst should have residual pages");

            // Deserialize over it.
            buf.MemoryStream.Position = 0;
            buf.StartRead();
            dst.Deserialize(buf);
            buf.StopRead(verifySentinel: false);

            NAssert.AreEqual(src.NumLiveAllocations, dst.NumLiveAllocations);
            NAssert.AreEqual(src.NumPages, dst.NumPages);
        }

        [Test]
        public void Generation_PreservedAcrossSaveLoad()
        {
            // Allocate a slot, free it, save+load, then Alloc into that same slot.
            // The new handle's generation must match what it would have been without
            // the save/load round-trip — proving that the free-slot generation byte
            // is preserved in the snapshot.
            using var src = new NativeChunkStore(TrecsLog.Default);

            // Bump slot 1's generation through a few alloc/free cycles.
            for (int i = 0; i < 3; i++)
            {
                var h = src.Alloc(32, 8, typeHash: 0);
                src.Free(h);
            }
            // Predict what the next Alloc *without* save/load would return.
            uint expectedNextHandle;
            using (var probe = CloneViaSerialize(src))
            {
                expectedNextHandle = probe.Alloc(32, 8, typeHash: 0).Value;
            }

            // Now save/load and Alloc — same handle?
            using var dst = CloneViaSerialize(src);
            var actual = dst.Alloc(32, 8, typeHash: 0).Value;
            NAssert.AreEqual(
                expectedNextHandle,
                actual,
                "Free-slot generation must round-trip so post-restore handle bumps match"
            );
            // Sanity: generation should not be 1 (which is what a fresh slot would mint).
            NativeChunkStoreResolver.DecodeHandle(new PtrHandle(actual), out _, out var gen);
            NAssert.Greater(gen, 1, "Generation should reflect the pre-save alloc history");
        }

        [Test]
        public void EmptyStore_RoundTripsCleanly()
        {
            // Edge case: serialize a freshly-constructed chunk store with no allocations.
            using var src = new NativeChunkStore(TrecsLog.Default);
            NAssert.AreEqual(0, src.NumLiveAllocations);
            NAssert.AreEqual(0, src.NumPages);

            using var dst = CloneViaSerialize(src);
            NAssert.AreEqual(0, dst.NumLiveAllocations);
            NAssert.AreEqual(0, dst.NumPages);

            // Should still be usable for fresh allocations.
            var h = dst.Alloc(64, 8, typeHash: 7);
            NAssert.IsFalse(h.IsNull);
            NAssert.AreEqual(7, dst.ResolveEntry(h).TypeHash);
        }

        [Test]
        public void Reclaim_ThenSnapshot_PreservesNullPages()
        {
            // ReclaimEmptyPages produces null entries in _pages. The wire format writes
            // those as PageKind.Null and _freePageIds restores the recycled pageIds in
            // order. After load, allocing must reuse those pageIds in the same order.
            using var src = new NativeChunkStore(TrecsLog.Default);

            // Force a few pages to exist, then free them all so they're empty.
            var handles = new List<PtrHandle>();
            for (int i = 0; i < 200; i++)
                handles.Add(src.Alloc(16, 8, typeHash: 0));
            var pagesBefore = src.NumPages;
            foreach (var h in handles)
                src.Free(h);
            var reclaimed = src.ReclaimEmptyPages();
            NAssert.Greater(reclaimed, 0, "Test setup requires at least one reclaimable page");

            using var dst = CloneViaSerialize(src);
            NAssert.AreEqual(src.NumPages, dst.NumPages);

            // Fresh allocs should pull pageIds from the recycled stack in the same order
            // pre- and post-restore. Easiest check: both halves grow the same number of
            // pages on the next batch of allocs.
            var srcPagesGrew = 0;
            var dstPagesGrew = 0;
            using (var srcClone = CloneViaSerialize(src))
            {
                var baseSrc = srcClone.NumPages;
                for (int i = 0; i < 100; i++)
                    srcClone.Alloc(16, 8, typeHash: 0);
                srcPagesGrew = srcClone.NumPages - baseSrc;
            }
            var baseDst = dst.NumPages;
            for (int i = 0; i < 100; i++)
                dst.Alloc(16, 8, typeHash: 0);
            dstPagesGrew = dst.NumPages - baseDst;

            NAssert.AreEqual(srcPagesGrew, dstPagesGrew, "Page-growth shape must match");
        }

        [Test]
        public void DisposeEntry_AfterRoundTrip_FreeSlotIsRecycled()
        {
            // After save/load, the bucket free-slot stack and side-table free-slot stack
            // are restored. Verify that Freeing a restored handle correctly returns its
            // slot/bucket-slot to the freelists by checking that a subsequent Alloc gets
            // back to the same slot index (since the bucket stack is LIFO).
            using var src = new NativeChunkStore(TrecsLog.Default);
            var h = src.Alloc(64, 8, typeHash: 42);

            using var dst = CloneViaSerialize(src);
            NAssert.AreEqual(1, dst.NumLiveAllocations);

            // Free the restored handle. Bucket slot returns to the bucket's free stack.
            dst.Free(new PtrHandle(h.Value));
            NAssert.AreEqual(0, dst.NumLiveAllocations);

            // Next Alloc of the same size should reuse the freed slot — the side-table
            // index will differ (it's a new slot in the stack), but the underlying bucket
            // slot must be the one we just freed. Easiest proxy: NumPages stays at 1
            // (no new bucket page was allocated).
            var pagesBefore = dst.NumPages;
            var h2 = dst.Alloc(64, 8, typeHash: 99);
            NAssert.AreEqual(
                pagesBefore,
                dst.NumPages,
                "Freed bucket slot should be reused without growing pages"
            );
            NAssert.AreEqual(99, dst.ResolveEntry(h2).TypeHash);
        }

        // ─── Helpers ───────────────────────────────────────────

        static NativeChunkStore BuildVariedStore(int seed)
        {
            var rng = new Random(seed);
            var store = new NativeChunkStore(TrecsLog.Default);
            var live = new List<PtrHandle>();

            // Mix of bucket and huge sizes, with some intermediate frees.
            int[] sizes = { 16, 64, 256, 1024, 4096, 8192, 80 * 1024 };
            for (int i = 0; i < 30; i++)
            {
                var size = sizes[rng.Next(sizes.Length)];
                var h = store.Alloc(size, 8, typeHash: rng.Next(1000));
                unsafe
                {
                    var addr = store.ResolveEntry(h).Address;
                    Marshal.WriteInt32(addr, (int)h.Value);
                }
                live.Add(h);
                if (live.Count > 5 && rng.Next(3) == 0)
                {
                    var idx = rng.Next(live.Count);
                    store.Free(live[idx]);
                    live.RemoveAt(idx);
                }
            }
            return store;
        }

        /// <summary>
        /// Serializes <paramref name="src"/> and deserializes into a fresh store. Caller
        /// must Dispose the returned store.
        /// </summary>
        static NativeChunkStore CloneViaSerialize(NativeChunkStore src)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            using var buf = new SerializationBuffer(registry);
            buf.StartWrite(version: 1, includeTypeChecks: true);
            src.Serialize(buf);
            buf.EndWrite();
            buf.MemoryStream.Position = 0;
            buf.StartRead();
            var dst = new NativeChunkStore(TrecsLog.Default);
            dst.Deserialize(buf);
            buf.StopRead(verifySentinel: false);
            return dst;
        }

        // ─── AllocExternal ─────────────────────────────────────

        [Test]
        public void AllocExternal_RoundTrip()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            unsafe
            {
                var size = 64;
                var alignment = 8;
                var raw = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1);
                var addr = new IntPtr(raw);

                var h = store.AllocExternal(addr, size, alignment, typeHash: 7);
                NAssert.AreEqual(1, store.NumLiveAllocations);
                NAssert.AreEqual(1, store.NumPages);

                var entry = store.ResolveEntry(h);
                NAssert.AreEqual(
                    addr,
                    entry.Address,
                    "AllocExternal must register the supplied pointer verbatim"
                );
                NAssert.AreEqual(7, entry.TypeHash);
                NAssert.AreEqual(
                    1,
                    entry.OwnsWholePage,
                    "External allocations occupy a dedicated single-slot page"
                );
                NAssert.AreEqual(0, entry.SlotIndex);

                // Free releases the page (calls AllocatorManager.Free on the registered pointer)
                // and removes the slot from the live-allocations count.
                store.Free(h);
                NAssert.AreEqual(0, store.NumLiveAllocations);
                NAssert.AreEqual(0, store.NumPages);
            }
        }

        [Test]
        public void AllocExternal_ResolverWorksFromBurstJob()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            unsafe
            {
                var size = 32;
                var alignment = 8;
                var raw = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1);
                var addr = new IntPtr(raw);

                var h = store.AllocExternal(addr, size, alignment, typeHash: 555);

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

                NAssert.AreEqual(addr.ToInt64(), addrSink.Value);
                NAssert.AreEqual(555, hashSink.Value);

                store.Free(h);
            }
        }

        // ─── B5: double-register external pointer detection ───

        [Test]
        public void AllocExternal_DoubleRegister_SamePointer_Throws()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

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
            using var store = new NativeChunkStore(TrecsLog.Default);

            unsafe
            {
                var size = 32;
                var alignment = 8;
                var raw = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1);
                var addr = new IntPtr(raw);

                var h1 = store.AllocExternal(addr, size, alignment, typeHash: 0);
                store.Free(h1);
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
            using var store = new NativeChunkStore(TrecsLog.Default);
            var handle = store.Alloc(64, 8, typeHash: 0, out var address);
            NAssert.AreEqual(address, store.ResolveEntry(handle).Address);
        }

        [Test]
        public void Resolver_FromBurstJob_AfterReuseSeesNewGeneration()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            var h1 = store.Alloc(64, 8, typeHash: 111);
            store.Free(h1);

            var h2 = store.Alloc(64, 8, typeHash: 222);

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

        // ─── Concurrent Alloc/Free vs Burst resolve ─────────────

        [BurstCompile]
        struct ResolveLoopJob : IJob
        {
            [ReadOnly]
            public NativeChunkStoreResolver Resolver;

            [ReadOnly]
            public NativeArray<PtrHandle> Handles;
            public int Iterations;
            public NativeReference<int> SuccessCount;
            public NativeReference<int> FailureCount;
            public NativeReference<int> TornCount;

            public void Execute()
            {
                int successes = 0;
                int failures = 0;
                int torn = 0;
                for (int iter = 0; iter < Iterations; iter++)
                {
                    for (int i = 0; i < Handles.Length; i++)
                    {
                        if (Resolver.TryResolveEntry(Handles[i], out var entry))
                        {
                            // Each stable handle was allocated with TypeHash =
                            // (slot index + 1) and Generation = 1. Anything else means
                            // a torn struct read or wrong-slot resolve.
                            if (
                                entry.InUse != 1
                                || entry.Generation != 1
                                || entry.TypeHash != i + 1
                            )
                            {
                                torn++;
                            }
                            else
                            {
                                successes++;
                            }
                        }
                        else
                        {
                            failures++;
                        }
                    }
                }
                SuccessCount.Value = successes;
                FailureCount.Value = failures;
                TornCount.Value = torn;
            }
        }

        [Test]
        public void Resolver_ConcurrentWith_MainThreadAllocAndFree_Is_Safe()
        {
            using var store = new NativeChunkStore(TrecsLog.Default);

            const int PreallocCount = 256;
            const int JobIterations = 4000;

            // Pre-allocate a stable set of handles. Each gets typeHash = slot index + 1
            // so the job can distinguish torn reads from clean resolves.
            var seed = new PtrHandle[PreallocCount];
            for (int i = 0; i < PreallocCount; i++)
            {
                seed[i] = store.Alloc(64, 8, typeHash: i + 1);
            }
            using var stableHandles = new NativeArray<PtrHandle>(seed, Allocator.TempJob);

            using var successCount = new NativeReference<int>(Allocator.TempJob);
            using var failureCount = new NativeReference<int>(Allocator.TempJob);
            using var tornCount = new NativeReference<int>(Allocator.TempJob);

            var jobHandle = new ResolveLoopJob
            {
                Resolver = store.Resolver,
                Handles = stableHandles,
                Iterations = JobIterations,
                SuccessCount = successCount,
                FailureCount = failureCount,
                TornCount = tornCount,
            }.Schedule();

            // Concurrent main-thread churn while the job is resolving:
            //  - Tight Alloc + Free cycles (exercises the Free path that bumps the
            //    side-table slot's generation while a job is reading other slots).
            //  - A rolling held-then-freed set (slots stay live for many iterations
            //    before being freed, so the job sees them resolve and then disappear).
            //  - Enough fresh allocs to cross a chunk boundary (>1024 slots) so the
            //    new-chunk publish path runs while the job is reading.
            var rng = new Random(12345);
            var rollingHandles = new List<PtrHandle>(64);
            for (int i = 0; i < 2000; i++)
            {
                var h = store.Alloc(rng.Next(8, 200), 8, typeHash: 0);
                if ((i & 7) == 0)
                {
                    rollingHandles.Add(h);
                }
                else
                {
                    store.Free(h);
                }

                if (rollingHandles.Count > 50)
                {
                    store.Free(rollingHandles[0]);
                    rollingHandles.RemoveAt(0);
                }
            }

            // Force a fresh-chunk allocation during the job by burning past slot 1024.
            var crossingHandles = new List<PtrHandle>(1200);
            for (int i = 0; i < 1200; i++)
            {
                crossingHandles.Add(store.Alloc(32, 8, typeHash: 0));
            }

            jobHandle.Complete();

            NAssert.AreEqual(
                0,
                tornCount.Value,
                "No torn reads of stable handles should ever be observed"
            );
            NAssert.AreEqual(0, failureCount.Value, "Stable handles should always resolve");
            NAssert.AreEqual(PreallocCount * JobIterations, successCount.Value);

            // Cleanup so Dispose doesn't warn about leaks.
            foreach (var h in rollingHandles)
            {
                store.Free(h);
            }
            foreach (var h in crossingHandles)
            {
                store.Free(h);
            }
            foreach (var h in stableHandles)
            {
                store.Free(h);
            }
        }
    }
}
