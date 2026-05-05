using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Trecs.Internal;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class RingDequeTests
    {
        [Test]
        public void TestMisc()
        {
            var buffer = new RingDeque<int>(5);
            Assert.AreEqual(0, buffer.Count);
            buffer.PushBack(1);
            Assert.AreEqual(1, buffer.Count);
            buffer.PushBack(2);
            Assert.AreEqual(1, buffer.PeekFront());
            Assert.AreEqual(1, buffer.PopFront());
            Assert.AreEqual(2, buffer.PeekFront());
            buffer.PushBack(3);
            Assert.AreEqual(2, buffer.PeekFront());
            Assert.AreEqual(2, buffer.Count);
        }

        [Test]
        public void TestCapacity()
        {
            var buffer = new RingDeque<int>(5);
            Assert.AreEqual(5, buffer.Capacity);
            buffer.EnsureCapacity(10);
            Assert.AreEqual(10, buffer.Capacity);
        }

        [Test]
        public void TestAutoResize()
        {
            var buffer = new RingDeque<int>(3);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);
            buffer.PushBack(4);
            Assert.AreEqual(4, buffer.Count);
            Assert.AreEqual(6, buffer.Capacity);
            Assert.AreEqual(1, buffer[0]);
            Assert.AreEqual(2, buffer[1]);
            Assert.AreEqual(3, buffer[2]);
            Assert.AreEqual(4, buffer[3]);
        }

        [Test]
        public void TestCircularBehavior()
        {
            var buffer = new RingDeque<int>(3);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);
            Assert.AreEqual(1, buffer.PopFront());
            buffer.PushBack(4);
            Assert.AreEqual(2, buffer[0]);
            Assert.AreEqual(3, buffer[1]);
            Assert.AreEqual(4, buffer[2]);
        }

        [Test]
        public void TestEmptyBuffer()
        {
            var buffer = new RingDeque<int>(3);
            Assert.Throws<InvalidOperationException>(() => buffer.PopFront());
            Assert.Throws<InvalidOperationException>(() => buffer.PeekFront());
        }

        [Test]
        public void TestIndexer()
        {
            var buffer = new RingDeque<int>(3);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);
            Assert.AreEqual(1, buffer[0]);
            Assert.AreEqual(2, buffer[1]);
            Assert.AreEqual(3, buffer[2]);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer[-1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = buffer[3]);
        }

        [Test]
        public void TestLargeNumberOfElements()
        {
            var buffer = new RingDeque<int>(10);
            var random = new Random();
            var expectedValues = new List<int>();

            for (int i = 0; i < 1000; i++)
            {
                if (buffer.Count > 0 && random.Next(4) == 0) // 25% chance to pop
                {
                    Assert.AreEqual(expectedValues[0], buffer.PeekFront());
                    int poppedValue = buffer.PopFront();
                    expectedValues.RemoveAt(0);
                }
                else // 75% chance to push
                {
                    int value = random.Next(1000);
                    buffer.PushBack(value);
                    expectedValues.Add(value);
                    if (expectedValues.Count > buffer.Capacity)
                    {
                        expectedValues.RemoveAt(0);
                    }
                }
            }

            Assert.AreEqual(expectedValues.Count, buffer.Count);
            for (int i = 0; i < buffer.Count; i++)
            {
                Assert.AreEqual(expectedValues[i], buffer[i]);
            }
        }

        [Test]
        public void TestLinqCompatibility()
        {
            var buffer = new RingDeque<int>(5);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);
            buffer.PushBack(4);
            buffer.PushBack(5);

            // Test LINQ methods
            Assert.AreEqual(15, buffer.Sum());
            Assert.AreEqual(5, buffer.Max());
            Assert.AreEqual(1, buffer.Min());
            Assert.AreEqual(3, buffer.Average());

            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, buffer.Where(x => x >= 2 && x <= 4));
            CollectionAssert.AreEqual(new[] { 2, 4 }, buffer.Where(x => x % 2 == 0));
        }

        [Test]
        public void TestForeachLoop()
        {
            var buffer = new RingDeque<int>(5);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);
            buffer.PushBack(4);
            buffer.PushBack(5);

            var list = new List<int>();

            foreach (var item in buffer)
            {
                list.Add(item);
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, list);
        }

        [Test]
        public void TestClear()
        {
            var buffer = new RingDeque<int>(5);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);

            buffer.Clear();
            Assert.AreEqual(0, buffer.Count);
            Assert.That(!buffer.Contains(1));
        }

        [Test]
        public void TestTryPopFront()
        {
            var buffer = new RingDeque<int>(3);
            buffer.PushBack(1);
            buffer.PushBack(2);

            Assert.That(buffer.TryPopFront(out int result));
            Assert.AreEqual(1, result);

            Assert.That(buffer.TryPopFront(out result));
            Assert.AreEqual(2, result);

            Assert.That(!buffer.TryPopFront(out result));
            Assert.AreEqual(0, result); // default value for int
        }

        [Test]
        public void TestModificationDuringEnumeration()
        {
            var buffer = new RingDeque<int>(5);
            buffer.PushBack(1);
            buffer.PushBack(2);
            buffer.PushBack(3);

            // Test modification during foreach
            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (var item in buffer)
                {
                    if (item == 2)
                    {
                        buffer.PushBack(4); // This should throw
                    }
                }
            });

            // Test modification during manual enumeration
            var enumerator = buffer.GetEnumerator();
            enumerator.MoveNext();
            Assert.AreEqual(1, enumerator.Current);

            buffer.PopFront(); // Modify the collection

            Assert.Throws<InvalidOperationException>(() =>
            {
                enumerator.MoveNext(); // This should throw
            });
        }
    }
}
