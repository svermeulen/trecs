#if ENABLE_UNITY_COLLECTIONS_CHECKS
using System;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Verifies that the per-allocation <c>AtomicSafetyHandle</c> attached to
    /// <see cref="TrecsListRead{T}"/> / <see cref="TrecsListWrite{T}"/> surfaces
    /// the cross-job conflicts Unity's safety walker catches on its own native
    /// containers: concurrent readers OK; two writers conflict; reader+writer
    /// conflict; struct-copy aliasing produces the same safety identity; and
    /// opening a wrapper after the list is disposed throws.
    /// </summary>
    [TestFixture]
    public class TrecsListSafetyTests
    {
        static (TrecsListHeap heap, NativeChunkStore chunkStore) CreateHeap()
        {
            var chunkStore = new NativeChunkStore(TrecsLog.Default);
            return (new TrecsListHeap(TrecsLog.Default, chunkStore), chunkStore);
        }

        [BurstCompile]
        struct ReadJob : IJob
        {
            [ReadOnly]
            public TrecsListRead<int> Read;
            public NativeReference<int> Sink;

            public void Execute()
            {
                Sink.Value = Read.Count > 0 ? Read[0] : -1;
            }
        }

        [BurstCompile]
        struct WriteJob : IJob
        {
            public TrecsListWrite<int> Write;
            public int Value;

            public void Execute()
            {
                Write.Add(Value);
            }
        }

        static void Flush(TrecsListHeap heap) => heap.FlushPendingOperations();

        [Test]
        public void TwoReaders_OnSameList_NoConflict()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var w = heap.Write(in list);
            w.Add(42);
            Flush(heap);

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = heap.Resolver.Read(in list), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = heap.Resolver.Read(in list), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(42, sinkA.Value);
            NAssert.AreEqual(42, sinkB.Value);

            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void TwoWriters_OnSameList_SecondScheduleThrows()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(16);
            Flush(heap);

            var first = new WriteJob { Write = heap.Resolver.Write(in list), Value = 1 }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = heap.Resolver.Write(in list), Value = 2 }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                heap.DisposeEntry(list.Handle.Value);
                Flush(heap);
                heap.Dispose();
            }
        }

        [Test]
        public void ReaderThenWriter_OnSameList_SecondScheduleThrows()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var w = heap.Write(in list);
            w.Add(0);
            Flush(heap);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var reader = new ReadJob { Read = heap.Resolver.Read(in list), Sink = sink }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = heap.Resolver.Write(in list), Value = 99 }.Schedule();
                });
            }
            finally
            {
                reader.Complete();
                heap.DisposeEntry(list.Handle.Value);
                Flush(heap);
                heap.Dispose();
            }
        }

        [Test]
        public void StructCopyAliasing_TwoWritersOnSameUnderlyingList_SecondScheduleThrows()
        {
            // The TrecsList<T> header struct is a POD wrapping a PtrHandle; copying it
            // produces two handles to the same backing storage. Both wrappers carry
            // the same per-handle AtomicSafetyHandle, so the walker rejects scheduling
            // them as concurrent writers.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(16);
            var aliasOfList = list;
            Flush(heap);

            var first = new WriteJob { Write = heap.Resolver.Write(in list), Value = 1 }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = heap.Resolver.Write(in aliasOfList),
                        Value = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                heap.DisposeEntry(list.Handle.Value);
                Flush(heap);
                heap.Dispose();
            }
        }

        [Test]
        public void OpeningWrapper_AfterDispose_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            Flush(heap);
            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);

            NAssert.Throws<TrecsException>(() => heap.Resolver.Read(in list));

            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ReadFromBurstJob_ObservesPriorWrites()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            var w = heap.Write(in list);
            w.Add(11);
            w.Add(22);
            w.Add(33);
            Flush(heap);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            new ReadJob { Read = heap.Resolver.Read(in list), Sink = sink }
                .Schedule()
                .Complete();
            NAssert.AreEqual(11, sink.Value);

            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void PreFlushDispose_WithInFlightJob_DrainsBeforeRelease()
        {
            // Regression for the pre-flush dispose path: a wrapper opened via the
            // main-thread pending-add path can still be sitting in a scheduled job.
            // The dispose must wait for that job to complete before releasing the
            // safety handle, otherwise the running job races with handle invalidation.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            // Pre-populate before flushing so the read job has something to read.
            var w = heap.Write(in list);
            w.Add(456);
            // Note: NOT flushed — entry is in _pendingAdds, not yet in _allEntries.

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = heap.Read(in list);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            heap.DisposeEntry(list.Handle.Value); // pre-flush path

            // DisposeEntry drained the job; .Complete() is a no-op.
            job.Complete();
            NAssert.AreEqual(456, sink.Value);

            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void WriteFromBurstJob_PersistsToList()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(16);
            Flush(heap);

            new WriteJob { Write = heap.Resolver.Write(in list), Value = 777 }
                .Schedule()
                .Complete();

            var read = heap.Read(in list);
            NAssert.AreEqual(1, read.Count);
            NAssert.AreEqual(777, read[0]);

            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [BurstCompile]
        struct GrowJob : IJob
        {
            public TrecsListWrite<int> Write;
            public int AddCount;

            public void Execute()
            {
                for (int i = 0; i < AddCount; i++)
                {
                    Write.Add(i);
                }
            }
        }

        [Test]
        public void AddInBurstJob_TriggersGrowAcrossCapacity()
        {
            // The wrapper caches a stable header pointer; Add re-reads
            // Data/Count/Capacity on every call and reallocates the data buffer
            // when Count==Capacity. Verifies the grow path runs end-to-end under
            // Burst (AllocatorManager.Allocate / MemCpy / Free), not just on
            // the main thread.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4); // small initial cap → multiple grows
            Flush(heap);

            const int n = 1000;
            new GrowJob { Write = heap.Resolver.Write(in list), AddCount = n }
                .Schedule()
                .Complete();

            var read = heap.Read(in list);
            NAssert.AreEqual(n, read.Count);
            NAssert.GreaterOrEqual(read.Capacity, n);
            for (int i = 0; i < n; i++)
            {
                NAssert.AreEqual(i, read[i], $"value at index {i}");
            }

            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverRead_TypeHashMismatch_Throws()
        {
            // Parity with the heap.Read mismatch test in TrecsListTests, but on
            // the Burst-side resolver path which goes through the
            // NativeDenseDictionary lookup.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            Flush(heap);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                heap.Resolver.Read(in bad);
            });

            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverWrite_TypeHashMismatch_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            Flush(heap);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                heap.Resolver.Write(in bad);
            });

            heap.DisposeEntry(list.Handle.Value);
            Flush(heap);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void HeapDispose_WithActiveWrapper_InvalidatesWrapper()
        {
            // heap.Dispose() releases every outstanding safety handle via
            // EnforceAllBufferJobsHaveCompletedAndRelease. Any wrapper still in
            // scope past Dispose carries a released handle, so the next access
            // throws (not crashes) on CheckReadAndThrow.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            var w = heap.Write(in list);
            w.Add(123);

            // Hold the wrapper across the heap's lifetime boundary.
            var read = heap.Read(in list);
            NAssert.AreEqual(123, read[0]); // works while heap is alive
            heap.Dispose();

            // After dispose, the wrapper's safety handle has been released —
            // CheckReadAndThrow throws ObjectDisposedException-shaped exception.
            NAssert.Catch(() =>
            {
                var _ = read[0];
            });
        }
    }
}
#endif
