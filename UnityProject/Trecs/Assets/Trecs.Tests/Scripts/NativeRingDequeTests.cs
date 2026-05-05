using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeRingDequeTests
    {
        [Test]
        public void TestPushPopBack()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            NAssert.AreEqual(0, deque.Length);
            NAssert.IsTrue(deque.IsEmpty);

            deque.PushBack(1);
            deque.PushBack(2);
            deque.PushBack(3);
            NAssert.AreEqual(3, deque.Length);

            NAssert.AreEqual(3, deque.PopBack());
            NAssert.AreEqual(2, deque.PopBack());
            NAssert.AreEqual(1, deque.PopBack());
            NAssert.IsTrue(deque.IsEmpty);
        }

        [Test]
        public void TestPushBackPopFrontIsFifo()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            deque.PushBack(1);
            deque.PushBack(2);
            deque.PushBack(3);

            NAssert.AreEqual(1, deque.PopFront());
            NAssert.AreEqual(2, deque.PopFront());
            NAssert.AreEqual(3, deque.PopFront());
        }

        [Test]
        public void TestPushFront()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            deque.PushFront(1);
            deque.PushFront(2);
            deque.PushFront(3);

            NAssert.AreEqual(3, deque.Length);
            NAssert.AreEqual(3, deque[0]);
            NAssert.AreEqual(2, deque[1]);
            NAssert.AreEqual(1, deque[2]);
        }

        [Test]
        public void TestMixedPushPop()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            deque.PushBack(2);
            deque.PushFront(1);
            deque.PushBack(3);
            deque.PushFront(0);

            NAssert.AreEqual(4, deque.Length);
            NAssert.AreEqual(0, deque[0]);
            NAssert.AreEqual(1, deque[1]);
            NAssert.AreEqual(2, deque[2]);
            NAssert.AreEqual(3, deque[3]);
        }

        [Test]
        public void TestPeekFrontAndBack()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            deque.PushBack(10);
            deque.PushBack(20);
            deque.PushBack(30);

            NAssert.AreEqual(10, deque.PeekFront());
            NAssert.AreEqual(30, deque.PeekBack());
            NAssert.AreEqual(3, deque.Length);
        }

        [Test]
        public void TestTryPopEmpty()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            NAssert.IsFalse(deque.TryPopFront(out _));
            NAssert.IsFalse(deque.TryPopBack(out _));
            NAssert.IsFalse(deque.TryPeekFront(out _));
            NAssert.IsFalse(deque.TryPeekBack(out _));
        }

        [Test]
        public void TestPopFromEmptyThrows()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            NAssert.Throws<InvalidOperationException>(() => deque.PopFront());
            NAssert.Throws<InvalidOperationException>(() => deque.PopBack());
            NAssert.Throws<InvalidOperationException>(() => deque.PeekFront());
            NAssert.Throws<InvalidOperationException>(() => deque.PeekBack());
        }

        [Test]
        public void TestAutoResize()
        {
            using var deque = new NativeRingDeque<int>(3, Allocator.Temp);
            deque.PushBack(1);
            deque.PushBack(2);
            deque.PushBack(3);
            deque.PushBack(4);

            NAssert.AreEqual(4, deque.Length);
            NAssert.AreEqual(6, deque.Capacity);
            NAssert.AreEqual(1, deque[0]);
            NAssert.AreEqual(2, deque[1]);
            NAssert.AreEqual(3, deque[2]);
            NAssert.AreEqual(4, deque[3]);
        }

        [Test]
        public void TestWrapAround()
        {
            using var deque = new NativeRingDeque<int>(3, Allocator.Temp);
            deque.PushBack(1);
            deque.PushBack(2);
            deque.PushBack(3);
            NAssert.AreEqual(1, deque.PopFront());
            deque.PushBack(4);

            NAssert.AreEqual(2, deque[0]);
            NAssert.AreEqual(3, deque[1]);
            NAssert.AreEqual(4, deque[2]);
        }

        [Test]
        public void TestGrowWhileWrapped()
        {
            using var deque = new NativeRingDeque<int>(3, Allocator.Temp);
            deque.PushBack(1);
            deque.PushBack(2);
            deque.PushBack(3);
            deque.PopFront();
            deque.PushBack(4);
            // Buffer is now full and wrapped; next push grows it.
            deque.PushBack(5);

            NAssert.AreEqual(4, deque.Length);
            NAssert.AreEqual(6, deque.Capacity);
            NAssert.AreEqual(2, deque[0]);
            NAssert.AreEqual(3, deque[1]);
            NAssert.AreEqual(4, deque[2]);
            NAssert.AreEqual(5, deque[3]);
        }

        [Test]
        public void TestIndexerSetter()
        {
            var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            try
            {
                deque.PushBack(1);
                deque.PushBack(2);
                deque.PushBack(3);

                deque[1] = 42;
                NAssert.AreEqual(42, deque[1]);
            }
            finally
            {
                deque.Dispose();
            }
        }

        [Test]
        public void TestElementAtRef()
        {
            var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            try
            {
                deque.PushBack(1);
                deque.PushBack(2);
                deque.PushBack(3);

                ref var slot = ref deque.ElementAt(1);
                slot = 99;
                NAssert.AreEqual(99, deque[1]);
            }
            finally
            {
                deque.Dispose();
            }
        }

        [Test]
        public void TestIndexerOutOfRangeThrows()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            deque.PushBack(1);
            NAssert.Throws<IndexOutOfRangeException>(() => _ = deque[-1]);
            NAssert.Throws<IndexOutOfRangeException>(() => _ = deque[1]);
        }

        [Test]
        public void TestForeach()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            for (int i = 1; i <= 5; i++)
            {
                deque.PushBack(i);
            }

            var collected = new List<int>();
            foreach (var item in deque)
            {
                collected.Add(item);
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, collected);
        }

        [Test]
        public void TestClear()
        {
            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            deque.PushBack(1);
            deque.PushBack(2);
            deque.Clear();

            NAssert.AreEqual(0, deque.Length);
            NAssert.IsTrue(deque.IsEmpty);
            NAssert.IsFalse(deque.TryPopFront(out _));
        }

        [Test]
        public void TestIsCreated()
        {
            NativeRingDeque<int> uninit = default;
            NAssert.IsFalse(uninit.IsCreated);
            NAssert.IsTrue(uninit.IsEmpty);

            using var deque = new NativeRingDeque<int>(5, Allocator.Temp);
            NAssert.IsTrue(deque.IsCreated);
        }

        [Test]
        public void TestDisposeReleases()
        {
            var deque = new NativeRingDeque<int>(5, Allocator.Persistent);
            deque.PushBack(1);
            deque.Dispose();
            NAssert.IsFalse(deque.IsCreated);
        }

        [Test]
        public void TestStressMixedOperations()
        {
            using var deque = new NativeRingDeque<int>(8, Allocator.Temp);
            var reference = new LinkedList<int>();
            var random = new Random(0);

            for (int step = 0; step < 2000; step++)
            {
                var op = random.Next(4);
                if (op == 0 && deque.Length > 0)
                {
                    NAssert.AreEqual(reference.First.Value, deque.PopFront());
                    reference.RemoveFirst();
                }
                else if (op == 1 && deque.Length > 0)
                {
                    NAssert.AreEqual(reference.Last.Value, deque.PopBack());
                    reference.RemoveLast();
                }
                else if (op == 2)
                {
                    var v = random.Next(10000);
                    deque.PushBack(v);
                    reference.AddLast(v);
                }
                else
                {
                    var v = random.Next(10000);
                    deque.PushFront(v);
                    reference.AddFirst(v);
                }
            }

            NAssert.AreEqual(reference.Count, deque.Length);
            int i = 0;
            foreach (var v in reference)
            {
                NAssert.AreEqual(v, deque[i++]);
            }
        }
    }
}
