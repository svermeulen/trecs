using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class CollectionSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
        }

        [Test]
        public void List_EmptyList_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int>();
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedList = _cacheHelper.ReadAll<List<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == 0);
        }

        [Test]
        public void List_WithElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int> { 1, 2, 3, 42, -5 };
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedList = _cacheHelper.ReadAll<List<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedList = _cacheHelper.ReadAll<List<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < 100; i++) // Check first 100 elements
            {
                Assert.That(deserializedList[i] == originalList[i]);
            }
        }

        [Test]
        public void Queue_EmptyQueue_SerializesAndDeserializes()
        {
            // Arrange
            var originalQueue = new Queue<int>();
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedQueue = _cacheHelper.ReadAll<Queue<int>>();

            // Assert
            Assert.IsNotNull(deserializedQueue);
            Assert.That(deserializedQueue.Count == 0);
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedQueue = _cacheHelper.ReadAll<Queue<int>>();

            // Assert
            Assert.IsNotNull(deserializedQueue);
            Assert.That(deserializedQueue.Count == expectedOrder.Length);
            for (int i = 0; i < expectedOrder.Length; i++)
            {
                Assert.That(
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalQueue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedQueue = _cacheHelper.ReadAll<Queue<int>>();

            // Assert
            Assert.IsNotNull(deserializedQueue);
            Assert.That(deserializedQueue.Count == 6);
            for (int i = 4; i < 10; i++)
            {
                Assert.That(deserializedQueue.Dequeue() == i);
            }
        }

        [Test]
        public void List_StringElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<string> { "hello", "world", "seriz", "" };
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedList = _cacheHelper.ReadAll<List<string>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
                    deserializedList[i] == originalList[i],
                    $"String at index {i} should match"
                );
            }
        }
    }
}
