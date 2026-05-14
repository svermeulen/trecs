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

        [Test]
        public void TwoReaders_OnSameList_NoConflict()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var w = heap.Write(in list);
            w.Add(42);

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = list.Read(heap.Resolver), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = list.Read(heap.Resolver), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(42, sinkA.Value);
            NAssert.AreEqual(42, sinkB.Value);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void TwoWriters_OnSameList_SecondScheduleThrows()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(16);

            var first = new WriteJob { Write = list.Write(heap.Resolver), Value = 1 }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = list.Write(heap.Resolver), Value = 2 }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                heap.DisposeEntry(list.Handle.Value);
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

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var reader = new ReadJob { Read = list.Read(heap.Resolver), Sink = sink }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = list.Write(heap.Resolver), Value = 99 }.Schedule();
                });
            }
            finally
            {
                reader.Complete();
                heap.DisposeEntry(list.Handle.Value);
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

            var first = new WriteJob { Write = list.Write(heap.Resolver), Value = 1 }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = aliasOfList.Write(heap.Resolver), Value = 2 }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                heap.DisposeEntry(list.Handle.Value);
                heap.Dispose();
            }
        }

        [Test]
        public void OpeningWrapper_AfterDispose_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            heap.DisposeEntry(list.Handle.Value);

            NAssert.Throws<TrecsException>(() => list.Read(heap.Resolver));

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

            using var sink = new NativeReference<int>(Allocator.TempJob);
            new ReadJob { Read = list.Read(heap.Resolver), Sink = sink }
                .Schedule()
                .Complete();
            NAssert.AreEqual(11, sink.Value);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeEntry_WithInFlightJob_ThrowsInEditor()
        {
            // Under the immediate-Free model, disposing a handle while a Burst job
            // still holds the safety handle throws InvalidOperationException via
            // CheckDeallocateAndThrow. Caller must complete in-flight jobs first.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var w = heap.Write(in list);
            w.Add(456);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = heap.Read(in list);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            // Job is still running with read's safety handle scheduled. Dispose must throw.
            NAssert.Throws<InvalidOperationException>(() => heap.DisposeEntry(list.Handle.Value));

            // Correct pattern: complete the job first, then dispose.
            job.Complete();
            NAssert.AreEqual(456, sink.Value);
            heap.DisposeEntry(list.Handle.Value);

            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_WithInFlightJob_ThrowsInEditor()
        {
            // EnsureCapacity reallocates the chunk-store-backed data slot and rewrites
            // header->Data. If we let it run while a Burst job is still resolving the
            // list, the job would dereference the freed slot. CheckDeallocateAndThrow on
            // the header's safety handle catches that.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            var w = heap.Write(in list);
            w.Add(1);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = heap.Read(in list);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            // Job is still running with read's safety handle scheduled. EnsureCapacity
            // must throw before it can free the old data slot underfoot.
            NAssert.Throws<InvalidOperationException>(() => heap.EnsureCapacity(in list, 64));

            // Correct pattern: complete the job first, then grow.
            job.Complete();
            NAssert.AreEqual(1, sink.Value);
            heap.EnsureCapacity(in list, 64);
            NAssert.GreaterOrEqual(heap.Read(in list).Capacity, 64);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void WriteFromBurstJob_PersistsToList()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(16);

            new WriteJob { Write = list.Write(heap.Resolver), Value = 777 }
                .Schedule()
                .Complete();

            var read = heap.Read(in list);
            NAssert.AreEqual(1, read.Count);
            NAssert.AreEqual(777, read[0]);

            heap.DisposeEntry(list.Handle.Value);
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
        public void AddInBurstJob_FillsPreSizedList()
        {
            // The wrapper caches a stable header pointer + cached data pointer set by
            // the main-thread EnsureCapacity (which reallocates the chunk-store-backed
            // data slot). The Burst job then fills it without ever touching the
            // allocator. Verifies the cached pointer remains valid across the schedule
            // boundary.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);

            const int n = 1000;
            heap.EnsureCapacity(in list, n);

            new GrowJob { Write = list.Write(heap.Resolver), AddCount = n }
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

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                bad.Read(heap.Resolver);
            });

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverWrite_TypeHashMismatch_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                bad.Write(heap.Resolver);
            });

            heap.DisposeEntry(list.Handle.Value);
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
