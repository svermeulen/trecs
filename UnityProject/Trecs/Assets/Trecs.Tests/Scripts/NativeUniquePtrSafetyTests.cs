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
    /// <see cref="NativeUniqueRead{T}"/> / <see cref="NativeUniqueWrite{T}"/>
    /// surfaces the same cross-job conflicts Unity's job-safety walker catches
    /// on its own native containers (concurrent writers, reader+writer, struct
    /// copies of the persistent <see cref="NativeUniquePtr{T}"/>).
    ///
    /// <para>The whole fixture is conditional on <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>
    /// — outside the editor / development builds, the safety system is compiled
    /// out and the calls under test are no-ops.</para>
    /// </summary>
    [TestFixture]
    public class NativeUniquePtrSafetyTests
    {
        static (
            NativeUniqueHeap heap,
            FrameScopedNativeUniqueHeap frameScopedHeap,
            NativeChunkStore chunkStore
        ) CreateHeap()
        {
            var chunkStore = new NativeChunkStore(TrecsLog.Default);
            var heap = new NativeUniqueHeap(TrecsLog.Default, chunkStore);
            var frameScopedHeap = new FrameScopedNativeUniqueHeap(TrecsLog.Default, chunkStore);
            return (heap, frameScopedHeap, chunkStore);
        }

        [BurstCompile]
        struct ReadJob : IJob
        {
            public NativeUniqueRead<int> Read;
            public NativeReference<int> Sink;

            public void Execute()
            {
                Sink.Value = Read.Value;
            }
        }

        [BurstCompile]
        struct WriteJob : IJob
        {
            public NativeUniqueWrite<int> Write;
            public int NewValue;

            public void Execute()
            {
                Write.Set(NewValue);
            }
        }

        [Test]
        public void TwoReaders_OnSamePtr_NoConflict()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(7);

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(7, sinkA.Value);
            NAssert.AreEqual(7, sinkB.Value);

            heap.DisposeEntry(ptr.Handle.Value);
            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void TwoWriters_OnSamePtr_SecondScheduleThrows()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(0);

            var first = new WriteJob
            {
                Write = heap.Resolver.Write(in ptr),
                NewValue = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = heap.Resolver.Write(in ptr), NewValue = 2 }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                heap.DisposeEntry(ptr.Handle.Value);
                heap.Dispose();
                frameScopedHeap.Dispose();
                chunkStore.Dispose();
            }
        }

        [Test]
        public void ReaderThenWriter_OnSamePtr_SecondScheduleThrows()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(0);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var reader = new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sink }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = heap.Resolver.Write(in ptr), NewValue = 9 }.Schedule();
                });
            }
            finally
            {
                reader.Complete();
                heap.DisposeEntry(ptr.Handle.Value);
                heap.Dispose();
                frameScopedHeap.Dispose();
                chunkStore.Dispose();
            }
        }

        [Test]
        public void StructCopyAliasing_TwoWritersOnSameUnderlyingBlob_SecondScheduleThrows()
        {
            // The persistent NativeUniquePtr<T> is a POD struct; copying it
            // produces two values that refer to the same heap address. The
            // safety handle is keyed by address, so both wrappers carry the
            // same handle — the walker rejects scheduling both as writers
            // even though the C# locals are independent.
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(0);
            var aliasOfPtr = ptr;

            var first = new WriteJob
            {
                Write = heap.Resolver.Write(in ptr),
                NewValue = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = heap.Resolver.Write(in aliasOfPtr),
                        NewValue = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                heap.DisposeEntry(ptr.Handle.Value);
                heap.Dispose();
                frameScopedHeap.Dispose();
                chunkStore.Dispose();
            }
        }

        [Test]
        public void FrameScoped_HandleReleasedAfterClearAndFlush()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = frameScopedHeap.Alloc<int>(frame: 0, value: 5);
            using var sink = new NativeReference<int>(Allocator.TempJob);
            new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sink }
                .Schedule()
                .Complete();
            NAssert.AreEqual(5, sink.Value);

            frameScopedHeap.ClearAtOrBeforeFrame(0);
            // The box and its safety handle are released at flush time, not at
            // ClearAtOrBeforeFrame — jobs scheduled before the flush may still
            // be reading. After flush, the handle's Release call drains any
            // outstanding jobs first.

            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void OpeningWrapper_AfterDispose_Throws()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(42);
            heap.DisposeEntry(ptr.Handle.Value);

            NAssert.Throws<TrecsException>(() => heap.Resolver.Read(in ptr));

            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverRead_TypeHashMismatch_Throws()
        {
            // Parity test: NativeUniquePtrResolver.Read must reject mismatched
            // type-hashes the same way the resolver's existing
            // ResolveUnsafePtr<T> path does.
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(42);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new NativeUniquePtr<float>(ptr.Handle);
                heap.Resolver.Read(in bad);
            });

            heap.DisposeEntry(ptr.Handle.Value);
            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverWrite_TypeHashMismatch_Throws()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(42);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new NativeUniquePtr<float>(ptr.Handle);
                heap.Resolver.Write(in bad);
            });

            heap.DisposeEntry(ptr.Handle.Value);
            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void HeapDispose_WithActiveWrapper_InvalidatesWrapper()
        {
            // heap.Dispose() releases every outstanding safety handle. Wrappers
            // held past Dispose carry a released handle and throw on next access.
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();
            var ptr = heap.Alloc<int>(7);

            var read = heap.Resolver.Read(in ptr);
            NAssert.AreEqual(7, read.Value); // works while alive

            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();

            NAssert.Catch(() =>
            {
                var _ = read.Value;
            });
        }

        [Test]
        public void FrameScoped_TwoWriters_OnSamePtr_SecondScheduleThrows()
        {
            // FrameScopedNativeUniqueHeap mints its own AtomicSafetyHandles via
            // the same per-address pattern — verify the walker catches a
            // write-vs-write conflict on a frame-scoped allocation just like on
            // a persistent one.
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = frameScopedHeap.Alloc<int>(frame: 0, value: 0);
            // Frame-scoped Alloc adds directly to _allEntries (no flush needed).

            var first = new WriteJob
            {
                Write = heap.Resolver.Write(in ptr),
                NewValue = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob { Write = heap.Resolver.Write(in ptr), NewValue = 2 }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                frameScopedHeap.ClearAtOrBeforeFrame(0);
                heap.Dispose();
                frameScopedHeap.Dispose();
                chunkStore.Dispose();
            }
        }

        [Test]
        public void FrameScoped_OpenAfterClear_Throws()
        {
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();
            var ptr = frameScopedHeap.Alloc<int>(frame: 0, value: 42);

            frameScopedHeap.ClearAtOrBeforeFrame(0);

            // The frame-scoped entry's safety handle was released at flush; the
            // resolver no longer has the entry, so Read throws TrecsException.
            NAssert.Throws<TrecsException>(() => heap.Resolver.Read(in ptr));

            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeEntry_WithInFlightJob_ThrowsInEditor()
        {
            // Under the immediate-Free model (matches NativeList<T>.Dispose() semantics),
            // freeing a handle while a Burst job still holds the safety handle throws
            // InvalidOperationException via CheckDeallocateAndThrow. The caller is
            // responsible for completing in-flight jobs before disposing the handle.
            var (heap, frameScopedHeap, chunkStore) = CreateHeap();

            var ptr = heap.Alloc<int>(123);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = heap.Read(in ptr);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            // DisposeEntry should throw because the job is still using the safety handle.
            NAssert.Throws<InvalidOperationException>(() => heap.DisposeEntry(ptr.Handle.Value));

            // Correct pattern: complete the job first, then dispose.
            job.Complete();
            NAssert.AreEqual(123, sink.Value);
            heap.DisposeEntry(ptr.Handle.Value);

            heap.Dispose();
            frameScopedHeap.Dispose();
            chunkStore.Dispose();
        }
    }
}
#endif
