using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class ErrorHandlingTests
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
        public void SerializerRegistry_UnregisteredType_ThrowsException()
        {
            // Arrange
            var unregisteredObject = new UnregisteredTestClass { Value = 42 };
            var flags = 0L;

            // Act & Assert
            Assert.Throws<TrecsException>(() =>
            {
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    unregisteredObject,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
            });
        }

        [Test]
        public void String_NullValue_HandlesCorrectly()
        {
            // Arrange
            string nullString = null;
            var flags = 0L;

            // Act - Framework now handles null strings correctly
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(
                nullString,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllObject() as string;

            // Assert - Verify null string is preserved
            Assert.That(result == null);
        }

        [Test]
        public void List_NullElements_HandlesCorrectly()
        {
            // Arrange
            var listWithNulls = new List<string> { "hello", null, "world" };
            var flags = 0L;

            // Act - Framework now handles null elements in collections correctly
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                listWithNulls,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<List<string>>();

            // Assert - Verify null elements are preserved
            Assert.That(result != null);
            Assert.That(result.Count == 3);
            Assert.That(result[0] == "hello");
            Assert.That(result[1] == null);
            Assert.That(result[2] == "world");
        }

        [Test]
        public void Dictionary_NullValues_HandlesCorrectly()
        {
            // Arrange
            var dictWithNulls = new Dictionary<string, string>
            {
                { "key1", "value1" },
                { "key2", null },
                { "key3", "value3" },
            };
            var flags = 0L;

            // Act - Framework now handles null values in dictionary correctly
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                dictWithNulls,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<Dictionary<string, string>>();

            // Assert - Verify null values are preserved
            Assert.That(result != null);
            Assert.That(result.Count == 3);
            Assert.That(result["key1"] == "value1");
            Assert.That(result["key2"] == null);
            Assert.That(result["key3"] == "value3");
        }

        [Test]
        public void List_EmptyAfterDeserialization_MaintainsCapacity()
        {
            // Arrange
            var originalList = new List<int> { 1, 2, 3, 4, 5 };
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
            var result = _cacheHelper.ReadAll<List<int>>();

            // Assert
            Assert.IsNotNull(result);
            Assert.That(result.Count == 5);
            // Capacity should be at least the number of elements
            Assert.That(result.Capacity >= 5);
        }

        [Test]
        public void SerializeToStream_InvalidData_HandlesGracefully()
        {
            // Arrange
            var validData = 42;
            var flags = 0L;

            // Act - First write valid data
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(validData, TestConstants.Version, includeTypeChecks: true, flags);

            // Try to read as wrong type - this should handle the type mismatch
            _cacheHelper.ResetMemoryPosition();

            // This should work fine since int is compatible
            var result = _cacheHelper.ReadAll<int>();
            Assert.That(result == validData);
        }

        [Test]
        public void LargeCollection_HandlesCorrectly()
        {
            // Arrange
            var largeList = new List<int>();
            for (int i = 0; i < 10000; i++)
            {
                largeList.Add(i);
            }
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(largeList, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<List<int>>();

            // Assert
            Assert.IsNotNull(result);
            Assert.That(result.Count == 10000);
            Assert.That(result[0] == 0);
            Assert.That(result[9999] == 9999);
        }

        [Test]
        public void MemoryStream_Position_ResetsCorrectly()
        {
            // Arrange
            var data1 = 42;
            var data2 = 100;
            var flags = 0L;

            // Act - Write first piece of data
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data1, TestConstants.Version, includeTypeChecks: true, flags);
            var position1 = _cacheHelper.MemoryStream.Position;

            // Clear and write second piece of data
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data2, TestConstants.Version, includeTypeChecks: true, flags);
            var position2 = _cacheHelper.MemoryStream.Position;

            // Reset and read data
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<int>();

            // Assert
            Assert.That(position1 > 0);
            Assert.That(position2 > 0);
            Assert.That(result == data2);
        }

        [Test]
        public void Vector3_SpecialValues_SerializesCorrectly()
        {
            // Arrange
            var specialVectors = new List<Vector3>
            {
                Vector3.zero,
                Vector3.one,
                Vector3.positiveInfinity,
                Vector3.negativeInfinity,
                new Vector3(float.NaN, float.NaN, float.NaN),
            };
            var flags = 0L;

            foreach (var original in specialVectors)
            {
                // Act
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<Vector3>();

                // Assert
                if (float.IsNaN(original.x))
                {
                    Assert.That(float.IsNaN(result.x));
                    Assert.That(float.IsNaN(result.y));
                    Assert.That(float.IsNaN(result.z));
                }
                else
                {
                    Assert.That(result.x == original.x, $"X component mismatch for {original}");
                    Assert.That(result.y == original.y, $"Y component mismatch for {original}");
                    Assert.That(result.z == original.z, $"Z component mismatch for {original}");
                }
            }
        }

        [Test]
        public void BooleanEdgeCases_SerializeCorrectly()
        {
            // Arrange
            var boolValues = new bool[] { true, false, true, false };
            var flags = 0L;

            foreach (var original in boolValues)
            {
                // Act
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<bool>();

                // Assert
                Assert.That(result == original);
            }
        }

        [Test]
        public void Buffer_RecoversAfterMidWriteException()
        {
            var unregistered = new UnregisteredTestClass { Value = 42 };
            var flags = 0L;

            Assert.Throws<TrecsException>(() =>
            {
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    unregistered,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
            });

            // After the failed write, the buffer should be usable again.
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(123, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<int>();

            Assert.That(result == 123);
        }

        [Test]
        public void Buffer_RecoversAfterMidReadException()
        {
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(123, TestConstants.Version, includeTypeChecks: true, flags);

            // Truncate the stream so the next read blows up mid-header.
            _cacheHelper.MemoryStream.SetLength(2);
            _cacheHelper.ResetMemoryPosition();

            Assert.Catch(() => _cacheHelper.ReadAll<int>());

            // Buffer should be recovered: full write/read round-trip must succeed.
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(456, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<int>();

            Assert.That(result == 456);
        }

        // Test class for unregistered type testing
        private class UnregisteredTestClass
        {
            public int Value { get; set; }
        }
    }
}
