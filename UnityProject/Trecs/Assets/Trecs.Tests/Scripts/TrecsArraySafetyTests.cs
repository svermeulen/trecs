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
    /// <see cref="TrecsArrayRead{T}"/> / <see cref="TrecsArrayWrite{T}"/> surfaces
    /// the cross-job conflicts Unity's safety walker catches on its own native
    /// containers: concurrent readers OK; two writers conflict; reader+writer
    /// conflict; struct-copy aliasing produces the same safety identity; and
    /// opening a wrapper after the array is disposed throws. Also runs through
    /// Burst to confirm the wrapper plumbing is actually Burst-compatible.
    /// </summary>
    [TestFixture]
    public class TrecsArraySafetyTests
    {
        static NativeHeap CreateChunkStore() => new NativeHeap(TrecsLog.Default);

        [BurstCompile]
        struct ReadJob : IJob
        {
            [ReadOnly]
            public TrecsArrayRead<int> Read;
            public NativeReference<int> Sink;
            public int Index;

            public void Execute()
            {
                Sink.Value = Read.Length > Index ? Read[Index] : -1;
            }
        }

        [BurstCompile]
        struct WriteJob : IJob
        {
            public TrecsArrayWrite<int> Write;
            public int Index;
            public int Value;

            public void Execute()
            {
                Write[Index] = Value;
            }
        }

        [Test]
        public void TwoReaders_OnSameArray_NoConflict()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 8);
            var w = arr.Write(chunkStore.Resolver);
            w[0] = 42;

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob
            {
                Read = arr.Read(chunkStore.Resolver),
                Sink = sinkA,
                Index = 0,
            }.Schedule();
            var b = new ReadJob
            {
                Read = arr.Read(chunkStore.Resolver),
                Sink = sinkB,
                Index = 0,
            }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(42, sinkA.Value);
            NAssert.AreEqual(42, sinkB.Value);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void TwoWriters_OnSameArray_SecondScheduleThrows()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 16);

            var first = new WriteJob
            {
                Write = arr.Write(chunkStore.Resolver),
                Index = 0,
                Value = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = arr.Write(chunkStore.Resolver),
                        Index = 1,
                        Value = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                arr.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void ReaderThenWriter_OnSameArray_SecondScheduleThrows()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 8);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var reader = new ReadJob
            {
                Read = arr.Read(chunkStore.Resolver),
                Sink = sink,
                Index = 0,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = arr.Write(chunkStore.Resolver),
                        Index = 0,
                        Value = 99,
                    }.Schedule();
                });
            }
            finally
            {
                reader.Complete();
                arr.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void StructCopyAliasing_TwoWritersOnSameUnderlyingArray_SecondScheduleThrows()
        {
            // The TrecsArray<T> handle is a POD wrapping a PtrHandle; copying it
            // produces two handles to the same backing storage. Both wrappers carry
            // the same per-handle AtomicSafetyHandle, so the walker rejects scheduling
            // them as concurrent writers.
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 16);
            var aliasOfArr = arr;

            var first = new WriteJob
            {
                Write = arr.Write(chunkStore.Resolver),
                Index = 0,
                Value = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = aliasOfArr.Write(chunkStore.Resolver),
                        Index = 1,
                        Value = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                arr.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void OpeningWrapper_AfterDispose_Throws()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 8);
            arr.Dispose(chunkStore);

            NAssert.Throws<TrecsException>(() => arr.Read(chunkStore.Resolver));

            chunkStore.Dispose();
        }

        [Test]
        public void ReadFromBurstJob_ObservesPriorWrites()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);
            var w = arr.Write(chunkStore.Resolver);
            w[0] = 11;
            w[1] = 22;
            w[2] = 33;

            using var sink = new NativeReference<int>(Allocator.TempJob);
            new ReadJob
            {
                Read = arr.Read(chunkStore.Resolver),
                Sink = sink,
                Index = 1,
            }
                .Schedule()
                .Complete();
            NAssert.AreEqual(22, sink.Value);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeEntry_WithInFlightJob_ThrowsInEditor()
        {
            // Under the immediate-Free model, disposing a handle while a Burst job
            // still holds the safety handle throws InvalidOperationException via
            // CheckDeallocateAndThrow. Caller must complete in-flight jobs first.
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 8);
            var w = arr.Write(chunkStore.Resolver);
            w[0] = 456;

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = arr.Read(chunkStore.Resolver);
            var job = new ReadJob
            {
                Read = read,
                Sink = sink,
                Index = 0,
            }.Schedule();

            // Job is still running with read's safety handle scheduled. Dispose must throw.
            NAssert.Throws<InvalidOperationException>(() => arr.Dispose(chunkStore));

            // Correct pattern: complete the job first, then dispose.
            job.Complete();
            NAssert.AreEqual(456, sink.Value);
            arr.Dispose(chunkStore);

            chunkStore.Dispose();
        }

        [Test]
        public void WriteFromBurstJob_PersistsToArray()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 16);

            new WriteJob
            {
                Write = arr.Write(chunkStore.Resolver),
                Index = 5,
                Value = 777,
            }
                .Schedule()
                .Complete();

            var read = arr.Read(chunkStore.Resolver);
            NAssert.AreEqual(777, read[5]);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [BurstCompile]
        struct FillJob : IJob
        {
            public TrecsArrayWrite<int> Write;

            public void Execute()
            {
                for (int i = 0; i < Write.Length; i++)
                {
                    Write[i] = i * 3;
                }
            }
        }

        [Test]
        public void FillInBurstJob_PopulatesEveryIndex()
        {
            // Exercises the indexer set path under Burst across the full Length, which
            // is the closest TrecsArray equivalent to TrecsList's AddInBurstJob test.
            var chunkStore = CreateChunkStore();
            const int n = 1000;
            var arr = TrecsArray.Alloc<int>(chunkStore, n);

            new FillJob { Write = arr.Write(chunkStore.Resolver) }
                .Schedule()
                .Complete();

            var read = arr.Read(chunkStore.Resolver);
            NAssert.AreEqual(n, read.Length);
            for (int i = 0; i < n; i++)
            {
                NAssert.AreEqual(i * 3, read[i], $"value at index {i}");
            }

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverRead_TypeHashMismatch_Throws()
        {
            // Parity with the mismatch test in TrecsArrayTests, on the Burst-side
            // resolver path: a wrong-T handle is rejected by the TypeId check inside
            // TrecsArray<T>.Read(in NativeHeapResolver).
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsArray<float>(arr.Handle, arr.Length);
                bad.Read(chunkStore.Resolver);
            });

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverWrite_TypeHashMismatch_Throws()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsArray<float>(arr.Handle, arr.Length);
                bad.Write(chunkStore.Resolver);
            });

            arr.Dispose(chunkStore);
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
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);
            var w = arr.Write(chunkStore.Resolver);
            w[0] = 123;

            // Hold the wrapper across the chunk store's lifetime boundary.
            var read = arr.Read(chunkStore.Resolver);
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
