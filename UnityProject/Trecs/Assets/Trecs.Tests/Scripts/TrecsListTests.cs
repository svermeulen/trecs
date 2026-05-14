using System.Runtime.InteropServices;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Functional tests for <see cref="TrecsList{T}"/> / <see cref="TrecsListHeap"/>:
    /// allocation, indexing, Add, growth, RemoveAt, RemoveAtSwapBack, Clear,
    /// EnsureCapacity, lifecycle, and main-thread Read/Write through the heap.
    /// Safety / Burst concurrency lives in
    /// <see cref="TrecsListSafetyTests"/>.
    /// </summary>
    [TestFixture]
    public class TrecsListTests
    {
        static (TrecsListHeap heap, NativeChunkStore chunkStore) CreateHeap()
        {
            var chunkStore = new NativeChunkStore(TrecsLog.Default);
            return (new TrecsListHeap(TrecsLog.Default, chunkStore), chunkStore);
        }

        [Test]
        public void Default_IsNull()
        {
            NAssert.IsTrue(default(TrecsList<int>).IsNull);
        }

        [Test]
        public void Alloc_ReturnsNonNullHandle_AndZeroCount()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>();

            NAssert.IsFalse(list.IsNull);
            var read = heap.Read(in list);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(0, read.Capacity);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void AllocWithInitialCapacity_PresizesBuffer()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(16);

            var read = heap.Read(in list);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(16, read.Capacity);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void Add_AppendsValues_AndUpdatesCount()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            var write = heap.Write(in list);

            write.Add(10);
            write.Add(20);
            write.Add(30);

            NAssert.AreEqual(3, write.Count);
            NAssert.AreEqual(10, write[0]);
            NAssert.AreEqual(20, write[1]);
            NAssert.AreEqual(30, write[2]);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_ThenAdd_FillsAcrossGrowBoundary()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(2);

            heap.EnsureCapacity(in list, 100);
            var write = heap.Write(in list);
            for (int i = 0; i < 100; i++)
            {
                write.Add(i);
            }

            NAssert.AreEqual(100, write.Count);
            NAssert.GreaterOrEqual(write.Capacity, 100);
            for (int i = 0; i < 100; i++)
            {
                NAssert.AreEqual(i, write[i], $"value at index {i}");
            }

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_FromZeroCapacity_AllocatesInitialBuffer()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>();
            NAssert.AreEqual(0, heap.Read(in list).Capacity);

            heap.EnsureCapacity(in list, 1);
            var write = heap.Write(in list);
            write.Add(42);
            NAssert.AreEqual(1, write.Count);
            NAssert.AreEqual(42, write[0]);
            NAssert.GreaterOrEqual(write.Capacity, 1);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void Add_PastCapacity_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(2);
            var write = heap.Write(in list);
            write.Add(1);
            write.Add(2);

            NAssert.Throws<TrecsException>(() => write.Add(3));

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void Indexer_WritesPersist()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            var write = heap.Write(in list);
            write.Add(0);
            write.Add(0);
            write.Add(0);

            write[1] = 99;
            NAssert.AreEqual(99, write[1]);
            NAssert.AreEqual(0, write[0]);
            NAssert.AreEqual(0, write[2]);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAt_ShiftsTailDown()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var write = heap.Write(in list);
            for (int i = 0; i < 5; i++)
                write.Add(i * 10);

            write.RemoveAt(1); // remove `10`
            NAssert.AreEqual(4, write.Count);
            NAssert.AreEqual(0, write[0]);
            NAssert.AreEqual(20, write[1]);
            NAssert.AreEqual(30, write[2]);
            NAssert.AreEqual(40, write[3]);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAtSwapBack_MovesLastIntoSlot()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var write = heap.Write(in list);
            write.Add(10);
            write.Add(20);
            write.Add(30);
            write.Add(40);

            write.RemoveAtSwapBack(1); // remove 20, replace with 40
            NAssert.AreEqual(3, write.Count);
            NAssert.AreEqual(10, write[0]);
            NAssert.AreEqual(40, write[1]);
            NAssert.AreEqual(30, write[2]);

            // Removing the last element should just decrement.
            write.RemoveAtSwapBack(2);
            NAssert.AreEqual(2, write.Count);
            NAssert.AreEqual(10, write[0]);
            NAssert.AreEqual(40, write[1]);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void Clear_ResetsCount_KeepsCapacity()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(8);
            var write = heap.Write(in list);
            write.Add(1);
            write.Add(2);
            write.Add(3);

            write.Clear();
            NAssert.AreEqual(0, write.Count);
            NAssert.AreEqual(8, write.Capacity);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_GrowsToAtLeastTarget()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);

            heap.EnsureCapacity(in list, 64);
            NAssert.GreaterOrEqual(heap.Read(in list).Capacity, 64);

            heap.EnsureCapacity(in list, 16); // no-op
            NAssert.GreaterOrEqual(heap.Read(in list).Capacity, 64);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void NumEntries_TracksAllocAndDispose()
        {
            var (heap, chunkStore) = CreateHeap();
            NAssert.AreEqual(0, heap.NumEntries);

            var a = heap.Alloc<int>();
            NAssert.AreEqual(1, heap.NumEntries);

            var b = heap.Alloc<float>();
            NAssert.AreEqual(2, heap.NumEntries);

            heap.DisposeEntry(a.Handle.Value);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry(b.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeEntry_BeforeFlush_FreesImmediately()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            NAssert.AreEqual(1, heap.NumEntries);

            heap.DisposeEntry(list.Handle.Value);
            NAssert.AreEqual(0, heap.NumEntries);

            // Should be safe to flush after — no leftover state.
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void TypeHashMismatch_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>();

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                heap.Read(in bad);
            });

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeUnknownHandle_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            NAssert.Throws<TrecsException>(() => heap.DisposeEntry(12345));
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void ResolvingNullHandle_Throws()
        {
            var (heap, chunkStore) = CreateHeap();
            var ptr = default(TrecsList<int>);
            NAssert.Throws<TrecsException>(() => heap.Read(in ptr));
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void Read_OnFreshlyAllocated_RoundTrips()
        {
            var (heap, chunkStore) = CreateHeap();

            var list = heap.Alloc<int>(4);
            var write = heap.Write(in list);
            write.Add(7);

            var read = heap.Read(in list);
            NAssert.AreEqual(1, read.Count);
            NAssert.AreEqual(7, read[0]);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void Read_AfterMultipleAdds_RoundTrips()
        {
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(4);
            var w = heap.Write(in list);
            w.Add(11);
            w.Add(22);

            var read = heap.Read(in list);
            NAssert.AreEqual(2, read.Count);
            NAssert.AreEqual(11, read[0]);
            NAssert.AreEqual(22, read[1]);

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_AcrossBucketBoundaries_PreservesContents()
        {
            // Crosses several chunk-store bucket boundaries (16→32→64→…→2048) to
            // exercise the data-slot reallocation path. Each grow performs a
            // _chunkStore.Alloc + MemCpy + _chunkStore.Free; the contents must survive
            // intact even though the underlying slot can land in a different bucket each
            // time.
            var (heap, chunkStore) = CreateHeap();
            var list = heap.Alloc<int>(2);
            var write = heap.Write(in list);
            write.Add(11);
            write.Add(22);

            heap.EnsureCapacity(in list, 500);
            write = heap.Write(in list);
            for (int i = 2; i < 500; i++)
            {
                write.Add(i * 7);
            }

            var read = heap.Read(in list);
            NAssert.AreEqual(500, read.Count);
            NAssert.AreEqual(11, read[0]);
            NAssert.AreEqual(22, read[1]);
            for (int i = 2; i < 500; i++)
            {
                NAssert.AreEqual(i * 7, read[i], $"value at index {i}");
            }

            heap.DisposeEntry(list.Handle.Value);
            heap.Dispose();
            chunkStore.Dispose();
        }

        [Test]
        public void StructLayout_IsFourBytes()
        {
            NAssert.AreEqual(4, Marshal.SizeOf<TrecsList<int>>());
            NAssert.AreEqual(4, Marshal.SizeOf<TrecsList<double>>());
        }

        // ── End-to-end through HeapAccessor ───────────────────────────────
        //
        // The tests above exercise TrecsListHeap directly. These walk the full
        // World → EcsHeapAllocator → HeapAccessor → TrecsListHeap chain so the
        // wiring stays honest.

        [Test]
        public void HeapAccessor_AllocReadWriteDispose_RoundTrips()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var list = TrecsList.Alloc<int>(heap, 8);
            NAssert.IsFalse(list.IsNull);
            NAssert.AreEqual(1, heap.TrecsListHeap.NumEntries);

            var write = list.Write(heap);
            write.Add(1);
            write.Add(2);
            write.Add(3);

            var read = list.Read(heap);
            NAssert.AreEqual(3, read.Count);
            NAssert.AreEqual(1, read[0]);
            NAssert.AreEqual(2, read[1]);
            NAssert.AreEqual(3, read[2]);

            list.Dispose(heap);
            NAssert.AreEqual(0, heap.TrecsListHeap.NumEntries);
        }

        [Test]
        public void HeapAccessor_AllocTrecsList_DefaultCapacityIsZero()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var heap = env.Accessor.Heap;

            var list = TrecsList.Alloc<int>(heap);
            var read = list.Read(heap);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(0, read.Capacity);

            list.Dispose(heap);
        }
    }
}
