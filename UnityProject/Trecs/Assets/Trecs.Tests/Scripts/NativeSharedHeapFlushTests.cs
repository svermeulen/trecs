using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeSharedHeapRefCountTests
    {
        static (NativeSharedHeap heap, BlobCache cache) CreateHeap()
        {
            var cache = new BlobCache(
                TrecsLog.Default,
                new BlobCacheSettings(),
                new NativeBlobBoxPool()
            );
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var factory = new BlobFactory(TrecsLog.Default, cache, registry);
            var heap = new NativeSharedHeap(TrecsLog.Default, cache, factory);
            return (heap, cache);
        }

        // The heap no longer exposes an eager CreateBlob; register a (constant) source and
        // acquire. Re-acquiring after dispose re-materializes from the source.
        static NativeSharedPtr<T> CreateBlob<T>(NativeSharedHeap heap, BlobId id, in T value)
            where T : unmanaged
        {
            heap.RegisterBlob(id, in value);
            return heap.GetBlob<T>(id);
        }

        [Test]
        public void DisposeAndReacquire_BlobStillResolvable()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(1), 42);

            heap.DecrementRef(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(1), out var ptr2));

            NAssert.AreEqual(42, heap.Read(in ptr2).Value);

            heap.DecrementRef(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeAndReacquire_ResolverWorksAfterReacquire()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(2), 99);

            heap.DecrementRef(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(2), out var ptr2));

            var read = heap.Read(in ptr2);
            NAssert.AreEqual(99, read.Value);

            heap.DecrementRef(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DoubleDisposeAndReacquire_Works()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(4), 17);

            // First cycle: dispose then reacquire
            heap.DecrementRef(ptr.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(4), out var ptr2));

            // Second cycle: dispose then reacquire again
            heap.DecrementRef(ptr2.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(4), out var ptr3));

            NAssert.AreEqual(17, heap.Read(in ptr3).Value);

            heap.DecrementRef(ptr3.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeReacquireDispose_FullyRemoves()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(5), 33);

            heap.DecrementRef(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(5), out var ptr2));

            heap.DecrementRef(ptr2.Handle);

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeAndReacquire_MaintainsCorrectCount()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(6), 55);

            heap.DecrementRef(ptr.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(6), out var ptr2));

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DecrementRef(ptr2.Handle);

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            cache.Dispose();
        }

        // ─── Concurrent visibility tests ─────────────────────────────

        [BurstCompile]
        struct ReadBlobJob : IJob
        {
            public NativeSharedPtrResolver Resolver;
            public uint Handle;
            public NativeReference<int> Result;

            public void Execute()
            {
                var entry = Resolver.ResolveEntry<int>(Handle);
                unsafe
                {
                    Result.Value = *(int*)entry.Address.ToPointer();
                }
            }
        }

        [Test]
        public void FreshlyAllocatedBlob_ImmediatelyVisibleToJob()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(100), 777);

            var result = new NativeReference<int>(0, Allocator.TempJob);
            new ReadBlobJob
            {
                Resolver = heap.Resolver,
                Handle = ptr.Handle,
                Result = result,
            }
                .Schedule()
                .Complete();

            NAssert.AreEqual(777, result.Value);

            result.Dispose();
            heap.DecrementRef(ptr.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── Slot reuse / generation tests ───────────────────────────

        [Test]
        public void StaleHandle_AfterFreeAndReuse_FailsResolve()
        {
            var (heap, cache) = CreateHeap();

            var ptr1 = CreateBlob<int>(heap, new BlobId(200), 10);
            var staleHandle = ptr1.Handle;

            heap.DecrementRef(ptr1.Handle);

            var ptr2 = CreateBlob<int>(heap, new BlobId(201), 20);

            NAssert.AreEqual(20, heap.Read(in ptr2).Value);

            var resolver = heap.Resolver;
            NAssert.IsFalse(resolver.TryResolveEntry(staleHandle, out _));

            heap.DecrementRef(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void SlotReuse_NewAllocationGetsRecycledSlot()
        {
            var (heap, cache) = CreateHeap();

            var ptr1 = CreateBlob<int>(heap, new BlobId(300), 10);
            heap.DecrementRef(ptr1.Handle);

            var ptr2 = CreateBlob<int>(heap, new BlobId(301), 20);

            // Handles differ (different generation) but both used the same slot index
            NAssert.AreNotEqual(ptr1.Handle, ptr2.Handle);
            NAssert.AreEqual(20, heap.Read(in ptr2).Value);

            heap.DecrementRef(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── Chunk boundary test ─────────────────────────────────────

        [Test]
        public void AllocatingPastChunkBoundary_AllBlobsResolvable()
        {
            var (heap, cache) = CreateHeap();
            const int count = 1100; // > 1024 entries per chunk

            var handles = new uint[count];
            for (int i = 0; i < count; i++)
            {
                var ptr = CreateBlob<int>(heap, new BlobId(i + 1), i);
                handles[i] = ptr.Handle;
            }

            NAssert.AreEqual(count, heap.NumEntries);

            var resolver = heap.Resolver;
            for (int i = 0; i < count; i++)
            {
                NAssert.IsTrue(resolver.TryResolveEntry(handles[i], out var entry));
                unsafe
                {
                    NAssert.AreEqual(i, *(int*)entry.Address.ToPointer());
                }
            }

            for (int i = 0; i < count; i++)
            {
                heap.DecrementRef(handles[i]);
            }

            NAssert.AreEqual(0, heap.NumEntries);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── GetBlobId test ──────────────────────────────────────────

        [Test]
        public void GetBlobId_ReturnsCorrectBlobId()
        {
            var (heap, cache) = CreateHeap();

            var blobId = new BlobId(42);
            var ptr = CreateBlob<int>(heap, blobId, 123);

            NAssert.AreEqual(blobId, heap.GetBlobId(ptr.Handle));

            heap.DecrementRef(ptr.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── Clone equality test ─────────────────────────────────────

        [Test]
        public void Clone_ReturnsSameHandleValue()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(50), 99);
            heap.TryClone<int>(ptr.Handle, out var clone);

            NAssert.AreEqual(ptr.Handle, clone.Handle);
            NAssert.AreEqual(ptr, clone);

            heap.DecrementRef(ptr.Handle);
            heap.DecrementRef(clone.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── Serialization round-trip tests ──────────────────────────

        static byte[] SerializeHeap(NativeSharedHeap heap)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var writer = new BinarySerializationWriter(registry);
            var serData = new SerializationData();
            writer.Start(serData, version: 1, includeTypeChecks: false);
            heap.Serialize(writer);
            writer.Complete();
            using var stream = new MemoryStream();
            serData.WriteContiguousTo(stream);
            return stream.ToArray();
        }

        static void DeserializeHeap(NativeSharedHeap heap, byte[] data)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var reader = new BinarySerializationReader(registry);
            reader.Start(new ContiguousSerializationData(data));
            heap.Deserialize(reader);
            reader.Complete();
        }

        /// <summary>
        /// Regression test for a snapshot determinism bug: the free-slot stack
        /// was serialized in pop order (Stack.ToArray) but restored by pushing
        /// in read order, which reversed it. Allocations after a reload then
        /// reused slots in a different order than a never-reloaded run — and
        /// since the slot index is baked into the handle, the two timelines
        /// (and their snapshot bytes) diverged.
        /// </summary>
        [Test]
        public void SerializeDeserialize_PreservesFreeSlotReuseOrder()
        {
            var (heap, cache) = CreateHeap();

            var p1 = CreateBlob<int>(heap, new BlobId(1), 1);
            var p2 = CreateBlob<int>(heap, new BlobId(2), 2);
            var p3 = CreateBlob<int>(heap, new BlobId(3), 3);
            var p4 = CreateBlob<int>(heap, new BlobId(4), 4);

            // Free three blobs so the free-slot stack is deeper than one entry
            // (a single-entry stack restores correctly even when reversed).
            heap.DecrementRef(p2.Handle);
            heap.DecrementRef(p3.Handle);
            heap.DecrementRef(p4.Handle);

            var bytes1 = SerializeHeap(heap);

            // Baseline: without any reload, the next allocation pops the stack top.
            var baseline = CreateBlob<int>(heap, new BlobId(5), 5);
            var baselineHandle = baseline.Handle;
            heap.DecrementRef(baseline.Handle);

            DeserializeHeap(heap, bytes1);

            // A freshly restored heap must serialize back to identical bytes...
            var bytes2 = SerializeHeap(heap);
            NAssert.AreEqual(bytes1, bytes2);

            // ...and must reuse free slots in the same order as the unreloaded
            // heap, yielding the same handle for the same allocation.
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(5), out var reloaded));
            NAssert.AreEqual(baselineHandle, reloaded.Handle);

            heap.DecrementRef(reloaded.Handle);
            heap.DecrementRef(p1.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── Reconciling deserialize tests ───────────────────────────
        //
        // Deserialize diffs the incoming entries against the live ones instead of
        // tearing everything down and rebuilding (the rollback path loads a snapshot
        // into the same world every frame). These cover the three reconcile outcomes:
        // unchanged slot (handle kept), changed slot (slot reused / blob moved since
        // capture), and stale slot (live now, absent from the snapshot).

        [Test]
        public void Deserialize_IntoSameHeap_KeepsUnchangedEntriesLive()
        {
            var (heap, cache) = CreateHeap();

            var p1 = CreateBlob<int>(heap, new BlobId(1), 11);
            var p2 = CreateBlob<int>(heap, new BlobId(2), 22);

            var bytes = SerializeHeap(heap);

            DeserializeHeap(heap, bytes);

            // Pre-load handles must still resolve — unchanged slots keep their
            // side-table entries and cache pins instead of being torn down and rebuilt.
            NAssert.AreEqual(2, heap.NumEntries);
            NAssert.AreEqual(11, heap.Read(in p1).Value);
            NAssert.AreEqual(22, heap.Read(in p2).Value);

            // And the reconciled heap must serialize back to identical bytes.
            NAssert.AreEqual(bytes, SerializeHeap(heap));

            heap.DecrementRef(p1.Handle);
            heap.DecrementRef(p2.Handle);
            NAssert.AreEqual(0, heap.NumEntries);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void Deserialize_IntoSameHeap_RevertsRefCounts()
        {
            var (heap, cache) = CreateHeap();

            var p1 = CreateBlob<int>(heap, new BlobId(1), 11);
            heap.TryClone<int>(p1.Handle, out var clone1); // refcount 2

            var bytes = SerializeHeap(heap);

            heap.TryClone<int>(p1.Handle, out _); // refcount 3, post-snapshot

            DeserializeHeap(heap, bytes);

            // Ref count reverted to the snapshot's 2: two decrements fully free it.
            heap.DecrementRef(p1.Handle);
            NAssert.AreEqual(1, heap.NumEntries);
            heap.DecrementRef(clone1.Handle);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void Deserialize_SlotReusedSinceSnapshot_RevertsToSnapshotState()
        {
            var (heap, cache) = CreateHeap();

            var p1 = CreateBlob<int>(heap, new BlobId(1), 11);
            var p2 = CreateBlob<int>(heap, new BlobId(2), 22);

            var bytes = SerializeHeap(heap);

            // Post-snapshot churn: drop blob 2, then allocate blob 3 — it reuses
            // blob 2's slot with a bumped generation, so the reload must take the
            // changed-slot path for that slot.
            heap.DecrementRef(p2.Handle);
            var p3 = CreateBlob<int>(heap, new BlobId(3), 33);

            DeserializeHeap(heap, bytes);

            NAssert.AreEqual(2, heap.NumEntries);
            NAssert.AreEqual(11, heap.Read(in p1).Value);
            // The snapshot-era handle for blob 2 is valid again...
            NAssert.AreEqual(22, heap.Read(in p2).Value);
            // ...and the post-snapshot handle for blob 3 is stale.
            NAssert.IsFalse(heap.TryClone<int>(p3.Handle, out _));

            NAssert.AreEqual(bytes, SerializeHeap(heap));

            heap.DecrementRef(p1.Handle);
            heap.DecrementRef(p2.Handle);
            NAssert.AreEqual(0, heap.NumEntries);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void Deserialize_EntryNotInSnapshot_IsSwept()
        {
            var (heap, cache) = CreateHeap();

            var p1 = CreateBlob<int>(heap, new BlobId(1), 11);
            var bytes = SerializeHeap(heap);

            // Allocate a second blob after the snapshot — on a fresh slot, so the
            // reload must sweep it rather than overwrite it.
            var p2 = CreateBlob<int>(heap, new BlobId(2), 22);

            DeserializeHeap(heap, bytes);

            NAssert.AreEqual(1, heap.NumEntries);
            NAssert.AreEqual(11, heap.Read(in p1).Value);
            NAssert.IsFalse(heap.TryClone<int>(p2.Handle, out _));

            NAssert.AreEqual(bytes, SerializeHeap(heap));

            heap.DecrementRef(p1.Handle);
            NAssert.AreEqual(0, heap.NumEntries);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void Deserialize_BlobsSwappedSlotsSinceSnapshot_Reconciles()
        {
            // The cross-mapping hazard: between capture and reload, both blobs moved to
            // each other's slot. The reconcile must not drop a blobId→slot mapping that
            // an earlier incoming entry already reassigned.
            var (heap, cache) = CreateHeap();

            var p1 = CreateBlob<int>(heap, new BlobId(1), 11);
            var p2 = CreateBlob<int>(heap, new BlobId(2), 22);

            var bytes = SerializeHeap(heap);

            // Free both (stack: slot of p2 on top), then re-acquire in the same order —
            // each blob pops the other's old slot.
            heap.DecrementRef(p1.Handle);
            heap.DecrementRef(p2.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(1), out var p1Moved));
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(2), out var p2Moved));
            NAssert.AreNotEqual(p1.Handle, p1Moved.Handle);

            DeserializeHeap(heap, bytes);

            // Snapshot-era handles valid again, post-churn handles stale.
            NAssert.AreEqual(11, heap.Read(in p1).Value);
            NAssert.AreEqual(22, heap.Read(in p2).Value);
            NAssert.IsFalse(heap.TryClone<int>(p1Moved.Handle, out _));
            NAssert.IsFalse(heap.TryClone<int>(p2Moved.Handle, out _));

            NAssert.AreEqual(bytes, SerializeHeap(heap));

            heap.DecrementRef(p1.Handle);
            heap.DecrementRef(p2.Handle);
            NAssert.AreEqual(0, heap.NumEntries);
            heap.Dispose();
            cache.Dispose();
        }

        // ─── Rollback generation determinism ───────────────────────────
        //
        // Generations are minted from side-table state (prior.Generation + 1) and are
        // serialized — in the dense payloads AND baked into every NativeSharedPtr handle
        // stored in component data. A slot first claimed after a snapshot must therefore
        // come back from that snapshot's load with generation 0 ("never minted"), or a
        // rolled-back-and-replayed world re-mints gen N+1 where a straight run mints
        // gen N — divergent snapshot bytes for identical logical state, i.e. a false
        // desync under per-frame checksum comparison.

        [Test]
        public void Deserialize_ReplayAfterRollback_MatchesStraightRunBytes()
        {
            // Rollback timeline: blob 1, snapshot, blob 2 on a post-snapshot fresh slot
            // (live at load → swept by the reconcile), load, replay blob 2's allocation.
            var (heapA, cacheA) = CreateHeap();
            var a1 = CreateBlob<int>(heapA, new BlobId(1), 11);
            var snapshot = SerializeHeap(heapA);
            CreateBlob<int>(heapA, new BlobId(2), 22);
            DeserializeHeap(heapA, snapshot);
            NAssert.IsTrue(heapA.TryGetBlob<int>(new BlobId(2), out var a2Replay));
            var rollbackBytes = SerializeHeap(heapA);

            // Straight-run timeline: the same logical end state, no rollback.
            var (heapB, cacheB) = CreateHeap();
            var b1 = CreateBlob<int>(heapB, new BlobId(1), 11);
            var b2 = CreateBlob<int>(heapB, new BlobId(2), 22);
            var straightBytes = SerializeHeap(heapB);

            // Without the fresh-region generation reset this diverges on blob 2's
            // generation: the rollback timeline re-mints gen 2, the straight run gen 1.
            NAssert.AreEqual(straightBytes, rollbackBytes);

            heapA.DecrementRef(a1.Handle);
            heapA.DecrementRef(a2Replay.Handle);
            heapA.Dispose();
            cacheA.Dispose();
            heapB.DecrementRef(b1.Handle);
            heapB.DecrementRef(b2.Handle);
            heapB.Dispose();
            cacheB.Dispose();
        }

        [Test]
        public void Deserialize_BitIdenticalFastPath_StillResetsFreshRegionGenerations()
        {
            // Alloc-then-free after the snapshot leaves the dense lists byte-identical
            // to the snapshot, so the load takes the whole-state fast path — no
            // reconcile, no sweep — yet the freed slot's side-table generation residue
            // would still make the replayed allocation mint gen 2. The fresh-region
            // reset must run on the fast path too.
            var (heapA, cacheA) = CreateHeap();
            var a1 = CreateBlob<int>(heapA, new BlobId(1), 11);
            var snapshot = SerializeHeap(heapA);
            var a2 = CreateBlob<int>(heapA, new BlobId(2), 22);
            heapA.DecrementRef(a2.Handle); // dense lists now byte-equal to the snapshot
            DeserializeHeap(heapA, snapshot);
            NAssert.IsTrue(heapA.TryGetBlob<int>(new BlobId(2), out var a2Replay));
            var rollbackBytes = SerializeHeap(heapA);

            var (heapB, cacheB) = CreateHeap();
            var b1 = CreateBlob<int>(heapB, new BlobId(1), 11);
            var b2 = CreateBlob<int>(heapB, new BlobId(2), 22);
            var straightBytes = SerializeHeap(heapB);

            NAssert.AreEqual(straightBytes, rollbackBytes);

            heapA.DecrementRef(a1.Handle);
            heapA.DecrementRef(a2Replay.Handle);
            heapA.Dispose();
            cacheA.Dispose();
            heapB.DecrementRef(b1.Handle);
            heapB.DecrementRef(b2.Handle);
            heapB.Dispose();
            cacheB.Dispose();
        }

        [Test]
        public void Clone_DisposingOriginal_CloneStillValid()
        {
            var (heap, cache) = CreateHeap();

            var ptr = CreateBlob<int>(heap, new BlobId(51), 88);
            heap.TryClone<int>(ptr.Handle, out var clone);

            heap.DecrementRef(ptr.Handle);

            NAssert.AreEqual(1, heap.NumEntries);
            NAssert.AreEqual(88, heap.Read(in clone).Value);

            heap.DecrementRef(clone.Handle);
            heap.Dispose();
            cache.Dispose();
        }
    }
}
