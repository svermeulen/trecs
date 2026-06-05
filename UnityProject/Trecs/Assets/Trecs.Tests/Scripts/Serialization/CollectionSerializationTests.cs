using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class CollectionSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void List_EmptyList_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int>();
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedList = _helper.ReadAll<List<int>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedList);
            NAssert.That(deserializedList.Count == 0);
        }

        [Test]
        public void List_WithElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int> { 1, 2, 3, 42, -5 };
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedList = _helper.ReadAll<List<int>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedList);
            NAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                NAssert.That(
                    deserializedList[i] == originalList[i],
                    $"Element at index {i} should match"
                );
            }
        }

        [Test]
        public void List_LargeList_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int>();
            for (int i = 0; i < 1000; i++)
            {
                originalList.Add(i * 2);
            }
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedList = _helper.ReadAll<List<int>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedList);
            NAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < 100; i++) // Check first 100 elements
            {
                NAssert.That(deserializedList[i] == originalList[i]);
            }
        }

        [Test]
        public void Queue_EmptyQueue_SerializesAndDeserializes()
        {
            // Arrange
            var originalQueue = new Queue<int>();
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedQueue = _helper.ReadAll<Queue<int>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedQueue);
            NAssert.That(deserializedQueue.Count == 0);
        }

        [Test]
        public void Queue_WithElements_SerializesAndPreservesFifoOrder()
        {
            // Arrange
            var originalQueue = new Queue<int>();
            int[] expectedOrder = { 1, 2, 3, 42, -5 };
            foreach (var item in expectedOrder)
            {
                originalQueue.Enqueue(item);
            }
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedQueue = _helper.ReadAll<Queue<int>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedQueue);
            NAssert.That(deserializedQueue.Count == expectedOrder.Length);
            for (int i = 0; i < expectedOrder.Length; i++)
            {
                NAssert.That(
                    deserializedQueue.Dequeue() == expectedOrder[i],
                    $"FIFO element at index {i} should match"
                );
            }
        }

        [Test]
        public void Queue_AfterDequeue_SerializesRemainingFifoOrder()
        {
            // Arrange — exercise the circular-buffer offset by dequeuing
            // some entries before serializing.
            var originalQueue = new Queue<int>();
            for (int i = 0; i < 10; i++)
            {
                originalQueue.Enqueue(i);
            }
            for (int i = 0; i < 4; i++)
            {
                originalQueue.Dequeue();
            }
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedQueue = _helper.ReadAll<Queue<int>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedQueue);
            NAssert.That(deserializedQueue.Count == 6);
            for (int i = 4; i < 10; i++)
            {
                NAssert.That(deserializedQueue.Dequeue() == i);
            }
        }

        [Test]
        public void IterableDictionaryManaged_ShrinkingPayloads_ScratchReuseKeepsExactCount()
        {
            // The managed dict serializer blits keys through a grow-only
            // scratch reused across calls on the registry-cached instance;
            // a smaller payload after a larger one must round-trip exactly
            // its own entries.
            var big = new IterableDictionary<int, string>();
            for (int i = 0; i < 100; i++)
            {
                big.Add(i, $"v{i}");
            }

            _helper.WriteAll(_data, big, TestConstants.Version, includeTypeChecks: true, 0L);
            var bigResult = _helper.ReadAll<IterableDictionary<int, string>>(_data);
            NAssert.That(bigResult.Count == 100);
            NAssert.That(bigResult[42] == "v42");

            var small = new IterableDictionary<int, string>();
            small.Add(7, "seven");
            small.Add(8, "eight");

            var smallData = new SerializationData();
            _helper.WriteAll(smallData, small, TestConstants.Version, includeTypeChecks: true, 0L);
            var smallResult = _helper.ReadAll<IterableDictionary<int, string>>(smallData);

            NAssert.That(smallResult.Count == 2);
            NAssert.That(smallResult[7] == "seven");
            NAssert.That(smallResult[8] == "eight");
        }

        [Test]
        public void Queue_ShrinkingPayloads_ScratchReuseKeepsExactCount()
        {
            // The serializer's staging scratch is grow-only and reused across
            // calls on the registry-cached instance; deserializing a smaller
            // queue after a larger one must not leak stale trailing scratch
            // elements into the result.
            var bigQueue = new Queue<int>();
            for (int i = 0; i < 100; i++)
            {
                bigQueue.Enqueue(i);
            }

            _helper.WriteAll(_data, bigQueue, TestConstants.Version, includeTypeChecks: true, 0L);
            var bigResult = _helper.ReadAll<Queue<int>>(_data);
            NAssert.That(bigResult.Count == 100);

            var smallQueue = new Queue<int>();
            smallQueue.Enqueue(7);
            smallQueue.Enqueue(8);
            smallQueue.Enqueue(9);

            var smallData = new SerializationData();
            _helper.WriteAll(
                smallData,
                smallQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                0L
            );
            var smallResult = _helper.ReadAll<Queue<int>>(smallData);

            NAssert.That(smallResult.Count == 3);
            NAssert.That(smallResult.Dequeue() == 7);
            NAssert.That(smallResult.Dequeue() == 8);
            NAssert.That(smallResult.Dequeue() == 9);
        }

        [Test]
        public void List_StringElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<string> { "hello", "world", "seriz", "" };
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedList = _helper.ReadAll<List<string>>(_data);

            // Assert
            NAssert.IsNotNull(deserializedList);
            NAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                NAssert.That(
                    deserializedList[i] == originalList[i],
                    $"String at index {i} should match"
                );
            }
        }
    }
}
