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
    /// <see cref="NativeTrecsListRead{T}"/> / <see cref="NativeTrecsListWrite{T}"/>
    /// surfaces the cross-job conflicts Unity's safety walker catches on its own
    /// native containers: concurrent readers OK; two writers conflict; reader+writer
    /// conflict; struct-copy aliasing produces the same safety identity; and
    /// opening a wrapper after the list is disposed throws.
    /// </summary>
    [TestFixture]
    public class TrecsListSafetyTests
    {
        static NativeHeap CreateChunkStore() => new NativeHeap(TrecsLog.Default);

        [BurstCompile]
        struct ReadJob : IJob
        {
            [ReadOnly]
            public NativeTrecsListRead<int> Read;
            public NativeReference<int> Sink;

            public void Execute()
            {
                Sink.Value = Read.Count > 0 ? Read[0] : -1;
            }
        }

        [BurstCompile]
        struct WriteJob : IJob
        {
            public NativeTrecsListWrite<int> Write;
            public int Value;

            public void Execute()
            {
                Write.Add(Value);
            }
        }

        [Test]
        public void TwoReaders_OnSameList_NoConflict()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var w = list.Write(chunkStore.Resolver);
            w.Add(42);

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = list.Read(chunkStore.Resolver), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = list.Read(chunkStore.Resolver), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(42, sinkA.Value);
            NAssert.AreEqual(42, sinkB.Value);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void TwoWriters_OnSameList_SecondScheduleThrows()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);

            var first = new WriteJob
            {
                Write = list.Write(chunkStore.Resolver),
                Value = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = list.Write(chunkStore.Resolver), Value = 2 }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                list.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void ReaderThenWriter_OnSameList_SecondScheduleThrows()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var w = list.Write(chunkStore.Resolver);
            w.Add(0);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var reader = new ReadJob
            {
                Read = list.Read(chunkStore.Resolver),
                Sink = sink,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = list.Write(chunkStore.Resolver), Value = 99 }.Schedule();
                });
            }
            finally
            {
                reader.Complete();
                list.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void StructCopyAliasing_TwoWritersOnSameUnderlyingList_SecondScheduleThrows()
        {
            // The TrecsList<T> header struct is a POD wrapping a PtrHandle; copying it
            // produces two handles to the same backing storage. Both wrappers carry
            // the same per-handle AtomicSafetyHandle, so the walker rejects scheduling
            // them as concurrent writers.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);
            var aliasOfList = list;

            var first = new WriteJob
            {
                Write = list.Write(chunkStore.Resolver),
                Value = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = aliasOfList.Write(chunkStore.Resolver),
                        Value = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                list.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void OpeningWrapper_AfterDispose_Throws()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            list.Dispose(chunkStore);

            NAssert.Throws<TrecsException>(() => list.Read(chunkStore.Resolver));

            chunkStore.Dispose();
        }

        [Test]
        public void ReadFromBurstJob_ObservesPriorWrites()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(11);
            w.Add(22);
            w.Add(33);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            new ReadJob { Read = list.Read(chunkStore.Resolver), Sink = sink }
                .Schedule()
                .Complete();
            NAssert.AreEqual(11, sink.Value);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeEntry_WithInFlightJob_ThrowsInEditor()
        {
            // Under the immediate-Free model, disposing a handle while a Burst job
            // still holds the safety handle throws InvalidOperationException via
            // CheckDeallocateAndThrow. Caller must complete in-flight jobs first.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var w = list.Write(chunkStore.Resolver);
            w.Add(456);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = list.Read(chunkStore.Resolver);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            // Job is still running with read's safety handle scheduled. Dispose must throw.
            NAssert.Throws<InvalidOperationException>(() => list.Dispose(chunkStore));

            // Correct pattern: complete the job first, then dispose.
            job.Complete();
            NAssert.AreEqual(456, sink.Value);
            list.Dispose(chunkStore);

            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_WithInFlightJob_ThrowsInEditor()
        {
            // EnsureCapacity reallocates the chunk-store-backed data slot and rewrites
            // header->Data. If we let it run while a Burst job is still resolving the
            // list, the job would dereference the freed slot. CheckDeallocateAndThrow on
            // the header's safety handle catches that.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(1);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = list.Read(chunkStore.Resolver);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            // Job is still running with read's safety handle scheduled. EnsureCapacity
            // must throw before it can free the old data slot underfoot.
            NAssert.Throws<InvalidOperationException>(() => list.EnsureCapacity(chunkStore, 64));

            // Correct pattern: complete the job first, then grow.
            job.Complete();
            NAssert.AreEqual(1, sink.Value);
            list.EnsureCapacity(chunkStore, 64);
            NAssert.GreaterOrEqual(list.Read(chunkStore.Resolver).Capacity, 64);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void WriteFromBurstJob_PersistsToList()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);

            new WriteJob { Write = list.Write(chunkStore.Resolver), Value = 777 }
                .Schedule()
                .Complete();

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(1, read.Count);
            NAssert.AreEqual(777, read[0]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [BurstCompile]
        struct GrowJob : IJob
        {
            public NativeTrecsListWrite<int> Write;
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
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);

            const int n = 1000;
            list.EnsureCapacity(chunkStore, n);

            new GrowJob { Write = list.Write(chunkStore.Resolver), AddCount = n }
                .Schedule()
                .Complete();

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(n, read.Count);
            NAssert.GreaterOrEqual(read.Capacity, n);
            for (int i = 0; i < n; i++)
            {
                NAssert.AreEqual(i, read[i], $"value at index {i}");
            }

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverRead_TypeHashMismatch_Throws()
        {
            // Parity with the mismatch test in TrecsListTests, on the Burst-side resolver
            // path: a wrong-T handle is rejected by the TypeId check inside
            // TrecsList<T>.Read(in NativeHeapResolver).
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                bad.Read(chunkStore.Resolver);
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverWrite_TypeHashMismatch_Throws()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                bad.Write(chunkStore.Resolver);
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ChunkStoreDispose_WithActiveWrapper_InvalidatesWrapper()
        {
            // The chunk store is what releases per-slot safety handles via
            // EnforceAllBufferJobsHaveCompletedAndRelease — so any wrapper still in
            // scope past chunk-store dispose carries a released handle, and the
            // next access throws on CheckReadAndThrow.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(123);

            // Hold the wrapper across the chunk store's lifetime boundary.
            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(123, read[0]); // works while chunk store is alive
            chunkStore.Dispose();

            // After chunk-store dispose, the wrapper's safety handle has been
            // released — CheckReadAndThrow throws an ObjectDisposedException-shaped
            // exception.
            NAssert.Catch(() =>
            {
                var _ = read[0];
            });
        }
    }
}
#endif
