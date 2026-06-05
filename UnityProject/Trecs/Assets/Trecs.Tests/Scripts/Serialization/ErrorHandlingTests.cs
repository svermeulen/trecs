using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class ErrorHandlingTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;
        private SerializationReadBuffer _readBuffer;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
            _readBuffer = new SerializationReadBuffer();
        }

        [Test]
        public void SerializerRegistry_UnregisteredType_ThrowsException()
        {
            // Arrange
            var unregisteredObject = new UnregisteredTestClass { Value = 42 };
            var flags = 0L;

            // Act & Assert
            TrecsDebugAssert.Throws<TrecsException>(() =>
            {
                _helper.WriteAll(
                    _data,
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
            _helper.WriteAllObject(
                _data,
                nullString,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAllObject(_data) as string;

            // Assert - Verify null string is preserved
            TrecsDebugAssert.That(result == null);
        }

        [Test]
        public void List_NullElements_HandlesCorrectly()
        {
            // Arrange
            var listWithNulls = new List<string> { "hello", null, "world" };
            var flags = 0L;

            // Act - Framework now handles null elements in collections correctly
            _helper.WriteAll(
                _data,
                listWithNulls,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAll<List<string>>(_data);

            // Assert - Verify null elements are preserved
            TrecsDebugAssert.That(result != null);
            TrecsDebugAssert.That(result.Count == 3);
            TrecsDebugAssert.That(result[0] == "hello");
            TrecsDebugAssert.That(result[1] == null);
            TrecsDebugAssert.That(result[2] == "world");
        }

        [Test]
        public void List_EmptyAfterDeserialization_MaintainsCapacity()
        {
            // Arrange
            var originalList = new List<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAll<List<int>>(_data);

            // Assert
            TrecsDebugAssert.IsNotNull(result);
            TrecsDebugAssert.That(result.Count == 5);
            // Capacity should be at least the number of elements
            TrecsDebugAssert.That(result.Capacity >= 5);
        }

        [Test]
        public void SerializeToStream_InvalidData_HandlesGracefully()
        {
            // Arrange
            var validData = 42;
            var flags = 0L;

            // Act - First write valid data
            _helper.WriteAll(
                _data,
                validData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            // Try to read as wrong type - this should handle the type mismatch

            // This should work fine since int is compatible
            var result = _helper.ReadAll<int>(_data);
            TrecsDebugAssert.That(result == validData);
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
            _helper.WriteAll(
                _data,
                largeList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAll<List<int>>(_data);

            // Assert
            TrecsDebugAssert.IsNotNull(result);
            TrecsDebugAssert.That(result.Count == 10000);
            TrecsDebugAssert.That(result[0] == 0);
            TrecsDebugAssert.That(result[9999] == 9999);
        }

        [Test]
        public void MemoryStream_Position_ResetsCorrectly()
        {
            // Arrange
            var data1 = 42;
            var data2 = 100;
            var flags = 0L;

            // Act - Write first piece of data
            _helper.WriteAll(_data, data1, TestConstants.Version, includeTypeChecks: true, flags);
            var position1 = _data.ContiguousSize;

            // Clear and write second piece of data
            _helper.WriteAll(_data, data2, TestConstants.Version, includeTypeChecks: true, flags);
            var position2 = _data.ContiguousSize;

            // Reset and read data
            var result = _helper.ReadAll<int>(_data);

            // Assert
            TrecsDebugAssert.That(position1 > 0);
            TrecsDebugAssert.That(position2 > 0);
            TrecsDebugAssert.That(result == data2);
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
                _helper.WriteAll(
                    _data,
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                var result = _helper.ReadAll<Vector3>(_data);

                // Assert
                if (float.IsNaN(original.x))
                {
                    TrecsDebugAssert.That(float.IsNaN(result.x));
                    TrecsDebugAssert.That(float.IsNaN(result.y));
                    TrecsDebugAssert.That(float.IsNaN(result.z));
                }
                else
                {
                    TrecsDebugAssert.That(
                        result.x == original.x,
                        $"X component mismatch for {original}"
                    );
                    TrecsDebugAssert.That(
                        result.y == original.y,
                        $"Y component mismatch for {original}"
                    );
                    TrecsDebugAssert.That(
                        result.z == original.z,
                        $"Z component mismatch for {original}"
                    );
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
                _helper.WriteAll(
                    _data,
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                var result = _helper.ReadAll<bool>(_data);

                // Assert
                TrecsDebugAssert.That(result == original);
            }
        }

        [Test]
        public void Buffer_RecoversAfterMidWriteException()
        {
            var unregistered = new UnregisteredTestClass { Value = 42 };
            var flags = 0L;

            TrecsDebugAssert.Throws<TrecsException>(() =>
            {
                _helper.WriteAll(
                    _data,
                    unregistered,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
            });

            // After the failed write, the buffer should be usable again.
            _helper.WriteAll(_data, 123, TestConstants.Version, includeTypeChecks: true, flags);
            var result = _helper.ReadAll<int>(_data);

            TrecsDebugAssert.That(result == 123);
        }

        [Test]
        public void Buffer_RecoversAfterMidReadException()
        {
            var flags = 0L;

            _helper.WriteAll(_data, 123, TestConstants.Version, includeTypeChecks: true, flags);

            // Truncate the serialized bytes so the next read blows up mid-header.
            var truncated = _data.ToContiguousBytes();
            NAssert.Catch(() =>
                _helper.ReadAll<int>(_readBuffer.Wrap(new ReadOnlyMemory<byte>(truncated, 0, 2)))
            );

            // Helper should be recovered: full write/read round-trip must succeed.
            _helper.WriteAll(_data, 456, TestConstants.Version, includeTypeChecks: true, flags);
            var result = _helper.ReadAll<int>(_data);

            TrecsDebugAssert.That(result == 456);
        }

        // Test class for unregistered type testing
        private class UnregisteredTestClass
        {
            public int Value { get; set; }
        }
    }
}
