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
        static NativeHeap CreateHeap()
        {
            var chunkStore = new NativeHeap(TrecsLog.Default);

            return chunkStore;
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
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 7);

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = ptr.Read(chunkStore.Resolver), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = ptr.Read(chunkStore.Resolver), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(7, sinkA.Value);
            NAssert.AreEqual(7, sinkB.Value);

            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void TwoWriters_OnSamePtr_SecondScheduleThrows()
        {
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 0);

            var first = new WriteJob
            {
                Write = ptr.Write(chunkStore.Resolver),
                NewValue = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = ptr.Write(chunkStore.Resolver),
                        NewValue = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                ptr.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void ReaderThenWriter_OnSamePtr_SecondScheduleThrows()
        {
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 0);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var reader = new ReadJob
            {
                Read = ptr.Read(chunkStore.Resolver),
                Sink = sink,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = ptr.Write(chunkStore.Resolver),
                        NewValue = 9,
                    }.Schedule();
                });
            }
            finally
            {
                reader.Complete();
                ptr.Dispose(chunkStore);
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
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 0);
            var aliasOfPtr = ptr;

            var first = new WriteJob
            {
                Write = ptr.Write(chunkStore.Resolver),
                NewValue = 1,
            }.Schedule();

            try
            {
                NAssert.Throws<InvalidOperationException>(() =>
                {
                    new WriteJob
                    {
                        Write = aliasOfPtr.Write(chunkStore.Resolver),
                        NewValue = 2,
                    }.Schedule();
                });
            }
            finally
            {
                first.Complete();
                ptr.Dispose(chunkStore);
                chunkStore.Dispose();
            }
        }

        [Test]
        public void OpeningWrapper_AfterDispose_Throws()
        {
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            ptr.Dispose(chunkStore);

            NAssert.Throws<TrecsException>(() => ptr.Read(chunkStore.Resolver));

            chunkStore.Dispose();
        }

        [Test]
        public void ResolverRead_TypeHashMismatch_Throws()
        {
            // Parity test: NativeUniquePtr.Read(in NativeHeapResolver) must reject
            // mismatched type-hashes the same way the main-thread Read path does.
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new NativeUniquePtr<float>(ptr.Handle);
                bad.Read(chunkStore.Resolver);
            });

            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ResolverWrite_TypeHashMismatch_Throws()
        {
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new NativeUniquePtr<float>(ptr.Handle);
                bad.Write(chunkStore.Resolver);
            });

            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void HeapDispose_WithActiveWrapper_InvalidatesWrapper()
        {
            // chunkStore.Dispose() releases every outstanding safety handle. Wrappers
            // held past Dispose carry a released handle and throw on next access.
            var chunkStore = CreateHeap();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 7);

            var read = ptr.Read(chunkStore.Resolver);
            NAssert.AreEqual(7, read.Value); // works while alive

            chunkStore.Dispose();

            NAssert.Catch(() =>
            {
                var _ = read.Value;
            });
        }

        [Test]
        public void DisposeEntry_WithInFlightJob_ThrowsInEditor()
        {
            // Under the immediate-Free model (matches NativeList<T>.Dispose() semantics),
            // freeing a handle while a Burst job still holds the safety handle throws
            // InvalidOperationException via CheckDeallocateAndThrow. The caller is
            // responsible for completing in-flight jobs before disposing the handle.
            var chunkStore = CreateHeap();

            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 123);

            using var sink = new NativeReference<int>(Allocator.TempJob);
            var read = ptr.Read(chunkStore.Resolver);
            var job = new ReadJob { Read = read, Sink = sink }.Schedule();

            // Dispose should throw because the job is still using the safety handle.
            NAssert.Throws<InvalidOperationException>(() => ptr.Dispose(chunkStore));

            // Correct pattern: complete the job first, then dispose.
            job.Complete();
            NAssert.AreEqual(123, sink.Value);
            ptr.Dispose(chunkStore);

            chunkStore.Dispose();
        }
    }
}
#endif
