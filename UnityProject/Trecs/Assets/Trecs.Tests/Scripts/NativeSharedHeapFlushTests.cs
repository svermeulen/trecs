using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeSharedHeapFlushTests
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
        public void DisposeAndReacquire_BeforeFlush_BlobStillResolvable()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(1), 42);
            heap.FlushPendingOperations();

            heap.DisposeHandle(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(1), out var ptr2));

            heap.FlushPendingOperations();

            NAssert.AreEqual(42, heap.Read(in ptr2).Value);

            heap.DisposeHandle(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeAndReacquire_BeforeFlush_ResolverWorksAfterFlush()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(2), 99);
            heap.FlushPendingOperations();

            heap.DisposeHandle(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(2), out var ptr2));

            heap.FlushPendingOperations();

            var read = heap.Read(in ptr2);
            NAssert.AreEqual(99, read.Value);

            heap.DisposeHandle(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeAndReacquire_NoIntermediateFlush_Works()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(3), 7);

            heap.DisposeHandle(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(3), out var ptr2));

            heap.FlushPendingOperations();

            NAssert.AreEqual(7, heap.Read(in ptr2).Value);
            NAssert.AreEqual(7, heap.Read(in ptr2).Value);

            heap.DisposeHandle(ptr2.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DoubleDisposeAndReacquire_BeforeFlush_Works()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(4), 17);
            heap.FlushPendingOperations();

            // First cycle: dispose then reacquire
            heap.DisposeHandle(ptr.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(4), out var ptr2));

            // Second cycle: dispose then reacquire again, still before flush
            heap.DisposeHandle(ptr2.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(4), out var ptr3));

            heap.FlushPendingOperations();

            NAssert.AreEqual(17, heap.Read(in ptr3).Value);

            heap.DisposeHandle(ptr3.Handle);
            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeReacquireDispose_FullyRemovesOnFlush()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(5), 33);
            heap.FlushPendingOperations();

            heap.DisposeHandle(ptr.Handle);

            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(5), out var ptr2));

            heap.DisposeHandle(ptr2.Handle);

            heap.FlushPendingOperations();

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            cache.Dispose();
        }

        [Test]
        public void DisposeAndReacquire_FlushMaintainsCorrectCount()
        {
            var (heap, cache) = CreateHeap();

            var ptr = heap.CreateBlob<int>(new BlobId(6), 55);
            heap.FlushPendingOperations();

            heap.DisposeHandle(ptr.Handle);
            NAssert.IsTrue(heap.TryGetBlob<int>(new BlobId(6), out var ptr2));

            heap.FlushPendingOperations();

            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeHandle(ptr2.Handle);
            heap.FlushPendingOperations();

            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            cache.Dispose();
        }
    }
}
