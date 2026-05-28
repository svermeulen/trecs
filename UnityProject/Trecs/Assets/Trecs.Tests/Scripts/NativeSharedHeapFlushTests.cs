using System.Collections.Generic;
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
            var blobStore = new BlobStoreInMemory(BlobStoreInMemorySettings.Default, null);
            var cache = new BlobCache(
                TrecsLog.Default,
                new List<IBlobStore> { blobStore },
                new BlobCacheSettings { SerializationVersion = 1 },
                new NativeBlobBoxPool()
            );
            var heap = new NativeSharedHeap(TrecsLog.Default, cache);
            return (heap, cache);
        }

        [Test]
        public void DisposeAndReacquire_BlobStillResolvable()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(1), 42);

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

            var ptr = heap.CreateBlob<int>(new BlobId(2), 99);

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

            var ptr = heap.CreateBlob<int>(new BlobId(4), 17);

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

            var ptr = heap.CreateBlob<int>(new BlobId(5), 33);

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

            var ptr = heap.CreateBlob<int>(new BlobId(6), 55);

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

            var ptr = heap.CreateBlob<int>(new BlobId(100), 777);

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

            var ptr1 = heap.CreateBlob<int>(new BlobId(200), 10);
            var staleHandle = ptr1.Handle;

            heap.DecrementRef(ptr1.Handle);

            var ptr2 = heap.CreateBlob<int>(new BlobId(201), 20);

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

            var ptr1 = heap.CreateBlob<int>(new BlobId(300), 10);
            heap.DecrementRef(ptr1.Handle);

            var ptr2 = heap.CreateBlob<int>(new BlobId(301), 20);

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
                var ptr = heap.CreateBlob<int>(new BlobId(i + 1), i);
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
            var ptr = heap.CreateBlob<int>(blobId, 123);

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

            var ptr = heap.CreateBlob<int>(new BlobId(50), 99);
            heap.TryClone<int>(ptr.Handle, out var clone);

            NAssert.AreEqual(ptr.Handle, clone.Handle);
            NAssert.AreEqual(ptr, clone);

            heap.DecrementRef(ptr.Handle);
            heap.DecrementRef(clone.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void Clone_DisposingOriginal_CloneStillValid()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(51), 88);
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
