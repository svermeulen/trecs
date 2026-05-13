using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Serialization.Internal;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
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
        public void Dictionary_Empty_SerializesAndDeserializes()
        {
            // Arrange
            var originalDict = new Dictionary<string, int>();
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalDict,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            _cacheHelper.ResetMemoryPosition();
            var deserializedDict = _cacheHelper.ReadAll<Dictionary<string, int>>();

            // Assert
            Assert.IsNotNull(deserializedDict);
            Assert.That(deserializedDict.Count == 0);
        }

        [Test]
        public void Dictionary_WithElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalDict = new Dictionary<string, int>
            {
                { "one", 1 },
                { "two", 2 },
                { "three", 3 },
                { "answer", 42 },
            };
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalDict,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedDict = _cacheHelper.ReadAll<Dictionary<string, int>>();

            // Assert
            Assert.IsNotNull(deserializedDict);
            Assert.That(deserializedDict.Count == originalDict.Count);
            foreach (var kvp in originalDict)
            {
                Assert.That(
                    deserializedDict.ContainsKey(kvp.Key),
                    $"Should contain key '{kvp.Key}'"
                );
                Assert.That(
                    deserializedDict[kvp.Key] == kvp.Value,
                    $"Value for key '{kvp.Key}' should match"
                );
            }
        }

        [Test]
        public void HashSet_Empty_SerializesAndDeserializes()
        {
            // Arrange
            var originalSet = new HashSet<int>();
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalSet,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedSet = _cacheHelper.ReadAll<HashSet<int>>();

            // Assert
            Assert.IsNotNull(deserializedSet);
            Assert.That(deserializedSet.Count == 0);
        }

        [Test]
        public void HashSet_WithElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalSet = new HashSet<int> { 1, 2, 3, 42, 100 };
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalSet,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedSet = _cacheHelper.ReadAll<HashSet<int>>();

            // Assert
            Assert.IsNotNull(deserializedSet);
            Assert.That(deserializedSet.Count == originalSet.Count);
            foreach (var item in originalSet)
            {
                Assert.That(deserializedSet.Contains(item), $"Should contain item {item}");
            }
        }

        [Test]
        public void HashSet_NoDuplicates_MaintainsUniqueness()
        {
            // Arrange
            var originalSet = new HashSet<int> { 1, 2, 3, 2, 1 }; // Duplicates should be ignored
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalSet,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedSet = _cacheHelper.ReadAll<HashSet<int>>();

            // Assert
            Assert.IsNotNull(deserializedSet);
            Assert.That(deserializedSet.Count == 3); // Only unique elements
            Assert.That(deserializedSet.Contains(1));
            Assert.That(deserializedSet.Contains(2));
            Assert.That(deserializedSet.Contains(3));
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

        [Test]
        public void Dictionary_IntKeys_SerializesAndDeserializes()
        {
            // Arrange
            var originalDict = new Dictionary<int, string>
            {
                { 1, "one" },
                { 2, "two" },
                { 42, "answer" },
                { -1, "negative" },
            };
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalDict,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedDict = _cacheHelper.ReadAll<Dictionary<int, string>>();

            // Assert
            Assert.IsNotNull(deserializedDict);
            Assert.That(deserializedDict.Count == originalDict.Count);
            foreach (var kvp in originalDict)
            {
                Assert.That(deserializedDict.ContainsKey(kvp.Key), $"Should contain key {kvp.Key}");
                Assert.That(
                    deserializedDict[kvp.Key] == kvp.Value,
                    $"Value for key {kvp.Key} should match"
                );
            }
        }
    }
}
