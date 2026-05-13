#if ENABLE_UNITY_COLLECTIONS_CHECKS
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Verifies that the per-BlobId <c>AtomicSafetyHandle</c> attached to
    /// <see cref="NativeSharedRead{T}"/> behaves the way Unity's job-safety
    /// walker expects on a read-only <c>[NativeContainer]</c>: concurrent
    /// readers OK, the same handle is shared across struct copies (so
    /// aliased <see cref="NativeSharedPtr{T}"/>s collapse to a single
    /// safety identity), and opening a wrapper after the blob's refcount
    /// drops to zero throws.
    ///
    /// <para>The whole fixture is conditional on <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>
    /// — outside the editor / development builds, the safety system is compiled
    /// out and these checks are no-ops.</para>
    /// </summary>
    [TestFixture]
    public class NativeSharedPtrSafetyTests
    {
        static (NativeSharedHeap heap, BlobCache cache) CreateHeap()
        {
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );
            var cache = new BlobCache(
                new List<IBlobStore> { blobStore },
                new BlobCacheSettings { CleanIntervalSeconds = 99999, SerializationVersion = 1 },
                new NativeBlobBoxPool()
            );
            var heap = new NativeSharedHeap(cache);
            return (heap, cache);
        }

        [BurstCompile]
        struct ReadJob : IJob
        {
            [ReadOnly]
            public NativeSharedRead<int> Read;
            public NativeReference<int> Sink;

            public void Execute()
            {
                Sink.Value = Read.Value;
            }
        }

        [Test]
        public void TwoReaders_OnSameBlob_NoConflict()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(1), 42);
            heap.FlushPendingOperations();

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(42, sinkA.Value);
            NAssert.AreEqual(42, sinkB.Value);

            heap.DisposeHandle(ptr.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void TwoReaders_OnSameBlob_FromClones_StillNoConflict()
        {
            // Cloning produces a new PtrHandle but the same BlobId; both
            // NativeSharedReads carry the same per-BlobId safety handle, so
            // Unity's walker still treats them as compatible readers.
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(2), 7);
            heap.TryClone<int>(ptr.Handle, out var clone);
            heap.FlushPendingOperations();

            using var sinkA = new NativeReference<int>(Allocator.TempJob);
            using var sinkB = new NativeReference<int>(Allocator.TempJob);

            var a = new ReadJob { Read = heap.Resolver.Read(in ptr), Sink = sinkA }.Schedule();
            var b = new ReadJob { Read = heap.Resolver.Read(in clone), Sink = sinkB }.Schedule();
            JobHandle.CombineDependencies(a, b).Complete();

            NAssert.AreEqual(7, sinkA.Value);
            NAssert.AreEqual(7, sinkB.Value);

            heap.DisposeHandle(ptr.Handle);
            heap.DisposeHandle(clone.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void OpeningWrapper_AfterAllRefsDisposed_Throws()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(3), 99);
            heap.FlushPendingOperations();
            heap.DisposeHandle(ptr.Handle);
            // FlushPendingOperations removes the entry from _allEntries AND
            // releases the safety handle (via EnforceAllBufferJobsHaveCompletedAndRelease).
            heap.FlushPendingOperations();

            NAssert.Throws<TrecsException>(() => heap.Resolver.Read(in ptr));

            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void ResolverRead_TypeHashMismatch_Throws()
        {
            // Parity test for NativeSharedPtrResolver.Read: must reject
            // mismatched type-hashes the same way ResolveUnsafePtr<T> does.
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(4), 99);
            heap.FlushPendingOperations();

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new NativeSharedPtr<float>(ptr.Handle, ptr.BlobId);
                heap.Resolver.Read(in bad);
            });

            heap.DisposeHandle(ptr.Handle);
            heap.FlushPendingOperations();
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void HeapDispose_WithActiveWrapper_InvalidatesWrapper()
        {
            // heap.Dispose() releases every outstanding safety handle. Wrappers
            // held past Dispose carry a released handle and throw on next access.
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(5), 17);
            heap.FlushPendingOperations();

            var read = heap.Resolver.Read(in ptr);
            NAssert.AreEqual(17, read.Value); // works while alive

            heap.Dispose();

            NAssert.Catch(() =>
            {
                var _ = read.Value;
            });

            cache.Dispose();
        }
    }
}
#endif
