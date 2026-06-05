using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeBagTests
    {
        #region Create / Dispose

        [Test]
        public void Create_IsEmpty()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            NAssert.IsTrue(bag.IsEmpty);
            NAssert.AreEqual(0, bag.Length);

            bag.Dispose();
        }

        [Test]
        public void Dispose_ThenDisposeAgain_NoThrow()
        {
            var bag = NativeBag.Create(Allocator.Persistent);
            bag.Dispose();
            // Second dispose should be safe (null check)
            bag.Dispose();
        }

        #endregion

        #region Enqueue / Dequeue — Single Type

        [Test]
        public void Enqueue_SingleItem_NotEmpty()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(42);

            // count is in bytes (int = 4 bytes), not item count
            NAssert.IsFalse(bag.IsEmpty);
            NAssert.Greater(bag.Length, 0);

            bag.Dispose();
        }

        [Test]
        public void Enqueue_Dequeue_ReturnsCorrectValue()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(42);
            var result = bag.Dequeue<int>();

            NAssert.AreEqual(42, result);

            bag.Dispose();
        }

        [Test]
        public void Enqueue_Multiple_DequeueInOrder()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(10);
            bag.Enqueue(20);
            bag.Enqueue(30);

            NAssert.AreEqual(10, bag.Dequeue<int>());
            NAssert.AreEqual(20, bag.Dequeue<int>());
            NAssert.AreEqual(30, bag.Dequeue<int>());

            bag.Dispose();
        }

        [Test]
        public void Enqueue_Dequeue_All_IsEmpty()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(1);
            bag.Enqueue(2);
            bag.Dequeue<int>();
            bag.Dequeue<int>();

            NAssert.IsTrue(bag.IsEmpty);

            bag.Dispose();
        }

        #endregion

        #region Enqueue / Dequeue — Mixed Types

        [Test]
        public void MixedTypes_EnqueueDequeue_CorrectValues()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(42);
            bag.Enqueue(3.14f);
            bag.Enqueue((uint)99);
            bag.Enqueue(true);

            NAssert.AreEqual(42, bag.Dequeue<int>());
            NAssert.AreEqual(3.14f, bag.Dequeue<float>(), 0.001f);
            NAssert.AreEqual(99u, bag.Dequeue<uint>());
            NAssert.AreEqual(true, bag.Dequeue<bool>());

            bag.Dispose();
        }

        [Test]
        public void MixedTypes_Struct_RoundTrips()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            var data = new TestVec { X = 1.5f, Y = 2.5f };
            bag.Enqueue(data);
            bag.Enqueue(42);

            var result = bag.Dequeue<TestVec>();
            NAssert.AreEqual(1.5f, result.X, 0.001f);
            NAssert.AreEqual(2.5f, result.Y, 0.001f);
            NAssert.AreEqual(42, bag.Dequeue<int>());

            bag.Dispose();
        }

        #endregion

        #region Clear

        [Test]
        public void Clear_MakesEmpty()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(1);
            bag.Enqueue(2);
            bag.Enqueue(3);

            bag.Clear();

            NAssert.IsTrue(bag.IsEmpty);

            bag.Dispose();
        }

        [Test]
        public void Clear_CanReuse()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(10);
            bag.Enqueue(20);
            bag.Clear();

            bag.Enqueue(30);

            NAssert.AreEqual(30, bag.Dequeue<int>());

            bag.Dispose();
        }

        #endregion

        #region ReserveEnqueue / AccessReserved

        [Test]
        public void ReserveEnqueue_ThenAccess_WritesCorrectly()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            ref var reserved = ref bag.ReserveEnqueue<int>(out var index);
            reserved = 99;

            // Access the reserved slot
            ref var accessed = ref bag.AccessReserved<int>(index);
            NAssert.AreEqual(99, accessed);

            // Can also dequeue it
            var dequeued = bag.Dequeue<int>();
            NAssert.AreEqual(99, dequeued);

            bag.Dispose();
        }

        [Test]
        public void ReserveEnqueue_UpdateLater_DequeuesUpdatedValue()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            ref var reserved = ref bag.ReserveEnqueue<int>(out var index);
            reserved = 10;

            // Update via AccessReserved
            ref var slot = ref bag.AccessReserved<int>(index);
            slot = 42;

            NAssert.AreEqual(42, bag.Dequeue<int>());

            bag.Dispose();
        }

        #endregion

        #region Growth / Capacity

        [Test]
        public void Enqueue_BeyondInitialCapacity_Grows()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            // Enqueue many items to force growth
            for (int i = 0; i < 100; i++)
            {
                bag.Enqueue(i);
            }

            NAssert.IsFalse(bag.IsEmpty);

            // Dequeue all and verify order
            for (int i = 0; i < 100; i++)
            {
                NAssert.AreEqual(i, bag.Dequeue<int>());
            }

            NAssert.IsTrue(bag.IsEmpty);

            bag.Dispose();
        }

        [Test]
        public void Enqueue_LargeStructs_GrowsCorrectly()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            for (int i = 0; i < 50; i++)
            {
                bag.Enqueue(new TestVec { X = i, Y = i * 2 });
            }

            NAssert.IsFalse(bag.IsEmpty);

            for (int i = 0; i < 50; i++)
            {
                var v = bag.Dequeue<TestVec>();
                NAssert.AreEqual((float)i, v.X, 0.001f);
                NAssert.AreEqual((float)(i * 2), v.Y, 0.001f);
            }

            bag.Dispose();
        }

        #endregion

        #region Wrap-Around

        [Test]
        public void WrapAround_EnqueueDequeueEnqueue_WorksCorrectly()
        {
            var bag = NativeBag.Create(Allocator.Persistent);

            // Fill, drain, refill — exercises the ring buffer wrap-around
            for (int i = 0; i < 20; i++)
                bag.Enqueue(i);
            for (int i = 0; i < 20; i++)
                NAssert.AreEqual(i, bag.Dequeue<int>());

            NAssert.IsTrue(bag.IsEmpty);

            // Re-enqueue after full drain (wraps around internal buffer)
            for (int i = 100; i < 120; i++)
                bag.Enqueue(i);
            for (int i = 100; i < 120; i++)
                NAssert.AreEqual(i, bag.Dequeue<int>());

            NAssert.IsTrue(bag.IsEmpty);

            bag.Dispose();
        }

        #endregion

        #region Wrap-Around Growth (regression)

        // Regression for a heap-overflow / data-loss bug in UnsafeBlob.Grow. When the
        // buffer grew while the live region was wrapped AND _readIndex % newCapacity
        // landed in the upper part of the new buffer, the relocation copied the wrapped
        // head segment past the end of the freshly-allocated buffer — overflowing the
        // heap and dropping an element (it read back as zero). Reproducing it requires a
        // grow that fires while the queue is NOT fully drained: a full drain resets both
        // heads to 0, which hides the wrapped-relocation path.
        [Test]
        public void Grow_WhileLiveDataWrapped_DeterministicRepro_PreservesValues()
        {
            // This sequence is hand-tuned against the growth constants
            // (newCapacity = (oldCapacity + sizeOf) << 1): the first long grows capacity
            // to 16 bytes; five enqueue/dequeue cycles advance the (unwrapped) read head
            // to 40 while keeping capacity pinned at 16; we then fill and force a grow to
            // 48 bytes. At that grow oldReaderHead == oldWriterHead == 8 (wrapped) and
            // newReaderHead = 40 % 48 = 40, so relocating the 16 live bytes runs to
            // offset 56 — 8 bytes past the 48-byte buffer.
            var bag = NativeBag.Create(Allocator.Persistent);

            bag.Enqueue(0L); // first enqueue grows capacity 0 -> 16

            // Five cycles: each enqueues one long (filling to capacity) then dequeues one,
            // advancing the read head by 8 bytes per cycle without ever fully draining.
            for (long i = 1; i <= 5; i++)
            {
                bag.Enqueue(i);
                NAssert.AreEqual(i - 1, bag.Dequeue<long>());
            }

            // Read head is now at unwrapped index 40, backlog holds {5}. Fill, then grow.
            bag.Enqueue(6L); // fills capacity (backlog {5, 6})
            bag.Enqueue(7L); // triggers the wrapped grow (backlog {5, 6, 7})

            // All three survivors must come back in FIFO order.
            NAssert.AreEqual(5L, bag.Dequeue<long>());
            NAssert.AreEqual(6L, bag.Dequeue<long>());
            NAssert.AreEqual(7L, bag.Dequeue<long>());

            NAssert.IsTrue(bag.IsEmpty);

            bag.Dispose();
        }

        [Test]
        public void Grow_WithSteadilyGrowingBacklog_PreservesFifoOrder()
        {
            // Broader coverage: enqueue two, dequeue one each cycle so the backlog keeps
            // growing (the buffer grows repeatedly) while the read head keeps advancing
            // and wrapping — exercising many grow-while-wrapped states with varied
            // read-head offsets. Never fully drains until the final sweep.
            var bag = NativeBag.Create(Allocator.Persistent);

            long nextToEnqueue = 0;
            long nextExpected = 0;
            int backlog = 0;

            for (int cycle = 0; cycle < 500; cycle++)
            {
                bag.Enqueue(nextToEnqueue++);
                bag.Enqueue(nextToEnqueue++);
                backlog += 2;

                NAssert.AreEqual(nextExpected++, bag.Dequeue<long>());
                backlog -= 1;
            }

            while (backlog > 0)
            {
                NAssert.AreEqual(nextExpected++, bag.Dequeue<long>());
                backlog -= 1;
            }

            NAssert.IsTrue(bag.IsEmpty);

            bag.Dispose();
        }

        #endregion
    }
}
