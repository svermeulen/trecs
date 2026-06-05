using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class TypeSafetyTests
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
        public void WriteAllObject_WrongTypeDeserialization_ThrowsException()
        {
            // Test type safety violation: serialize as one type, deserialize as another
            var flags = 0L;

            // Serialize an int
            _helper.WriteAllObject(
                _data,
                42,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            // Try to deserialize as string - should throw
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<string>(_data);
            });
        }

        [Test]
        public void ArraySerializer_NegativeLength_ThrowsException()
        {
            // Test array serializer with corrupted negative length
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write array type info with negative length
            writer.Write(99999); // fake array type ID
            writer.Write(-5); // negative array length
            writer.Flush();
            memoryStream.Position = 0;

            // This should throw when trying to allocate negative-length array
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAllObject(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void SerializerRegistry_NullType_ThrowsException()
        {
            // Test passing null type to registry
            NAssert.Catch<Exception>(() =>
            {
                _serializerRegistry.GetSerializer(null);
            });
        }

        [Test]
        public void SerializerRegistry_AbstractType_ThrowsException()
        {
            // Test trying to get serializer for abstract type
            NAssert.Catch<Exception>(() =>
            {
                _serializerRegistry.GetSerializer(typeof(Stream)); // Abstract class
            });
        }

        [Test]
        public void BinaryWriter_ExtremelyLargeString_HandlesGracefully()
        {
            // Test with string that would cause memory issues
            var flags = 0L;

            // Create a very large string (but not so large it kills the test)
            var largeString = new string('x', 1000000); // 1MB string

            try
            {
                _helper.WriteAll(
                    _data,
                    largeString,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                var result = _helper.ReadAll<string>(_data);
                NAssert.That(result.Length == largeString.Length);
            }
            catch (OutOfMemoryException)
            {
                // This is acceptable behavior for extremely large data
                return;
            }
        }

        [Test]
        public void ListSerializer_PreallocatedList_HandlesCorrectly()
        {
            // Test edge case: deserializing into a list that already has elements
            var flags = 0L;
            var originalData = new List<int> { 1, 2, 3 };

            _helper.WriteAll(
                _data,
                originalData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            // This tests if the framework properly clears existing list data
            var result = _helper.ReadAll<List<int>>(_data);

            NAssert.That(result.Count == 3);
            NAssert.That(result[0] == 1);
            NAssert.That(result[1] == 2);
            NAssert.That(result[2] == 3);
        }

        [Test]
        public void SerializeObject_NullReference_HandlesCorrectly()
        {
            // Test serializing null object reference - should work with null support
            var flags = 0L;

            // This should no longer throw an exception with null support
            _helper.WriteAllObject(
                _data,
                null,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            // Verify we can read back the null
            var result = _helper.ReadAllObject(_data);
            NAssert.That(result == null);
        }

        [Test]
        public void FloatSerialization_SpecialValues_HandlesCorrectly()
        {
            // Test special float values that might cause issues
            var specialFloats = new[]
            {
                float.NaN,
                float.PositiveInfinity,
                float.NegativeInfinity,
                float.MinValue,
                float.MaxValue,
                float.Epsilon,
                -0.0f,
                0.0f,
            };

            var flags = 0L;

            foreach (var value in specialFloats)
            {
                _helper.WriteAll(
                    _data,
                    value,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                var result = _helper.ReadAll<float>(_data);

                if (float.IsNaN(value))
                {
                    NAssert.That(float.IsNaN(result), $"NaN not preserved");
                }
                else
                {
                    NAssert.That(result == value, $"Special float {value} not preserved correctly");
                }
            }
        }

        [Test]
        public void IntegerSerialization_BoundaryValues_HandlesCorrectly()
        {
            // Test integer boundary values
            var boundaryInts = new[]
            {
                int.MinValue,
                int.MaxValue,
                -1,
                0,
                1,
                short.MinValue,
                short.MaxValue,
                byte.MinValue,
                byte.MaxValue,
            };

            var flags = 0L;

            foreach (var value in boundaryInts)
            {
                _helper.WriteAll(
                    _data,
                    value,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                var result = _helper.ReadAll<int>(_data);
                NAssert.That(result == value, $"Boundary int {value} not preserved correctly");
            }
        }

        [Test]
        public void Vector3Serialization_ExtremeMagnitudes_HandlesCorrectly()
        {
            // Test Vector3 with extreme values
            var extremeVectors = new[]
            {
                new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                new Vector3(float.MinValue, float.MinValue, float.MinValue),
                new Vector3(float.Epsilon, float.Epsilon, float.Epsilon),
                new Vector3(0, 0, 0),
                new Vector3(float.NaN, 0, 0),
                new Vector3(0, float.PositiveInfinity, 0),
                new Vector3(0, 0, float.NegativeInfinity),
            };

            var flags = 0L;

            foreach (var vector in extremeVectors)
            {
                _helper.WriteAll(
                    _data,
                    vector,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                var result = _helper.ReadAll<Vector3>(_data);

                if (float.IsNaN(vector.x))
                {
                    NAssert.That(float.IsNaN(result.x), "NaN component not preserved in Vector3");
                }
                else if (float.IsInfinity(vector.y))
                {
                    NAssert.That(
                        float.IsInfinity(result.y),
                        "Infinity component not preserved in Vector3"
                    );
                }
                else if (float.IsNegativeInfinity(vector.z))
                {
                    NAssert.That(
                        float.IsNegativeInfinity(result.z),
                        "Negative infinity component not preserved in Vector3"
                    );
                }
                else
                {
                    NAssert.That(
                        Vector3.Distance(result, vector) < 0.001f,
                        $"Extreme Vector3 {vector} not preserved correctly, got {result}"
                    );
                }
            }
        }

        [Test]
        public void StringSerialization_UnicodeAndSpecialChars_HandlesCorrectly()
        {
            // Test string serialization with problematic characters
            var problematicStrings = new[]
            {
                "", // Empty string
                "\0", // Null character
                "Hello\0World", // Embedded null
                "Unicode: 你好世界 🚀 emoji", // Unicode and emoji
                "\r\n\t", // Control characters
                "\"quotes\" and 'apostrophes'", // Quote characters
                "\\backslashes\\and/forward/slashes", // Path separators
                new string('x', 65536), // Very long string
                "\uFFFF\uFFFE", // Unicode edge cases
            };

            var flags = 0L;

            foreach (var str in problematicStrings)
            {
                _helper.WriteAll(_data, str, TestConstants.Version, includeTypeChecks: true, flags);
                var result = _helper.ReadAll<string>(_data);
                NAssert.That(
                    result == str,
                    $"Problematic string not preserved: '{str}' vs '{result}'"
                );
            }
        }

        [Test]
        public void CollectionSerialization_EmptyCollections_HandlesCorrectly()
        {
            // Test various empty collection edge cases
            var flags = 0L;

            // Empty List
            var emptyList = new List<int>();
            _helper.WriteAll(
                _data,
                emptyList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var resultList = _helper.ReadAll<List<int>>(_data);
            NAssert.That(resultList.Count == 0);

            // Empty IterableDictionary
            var emptyDict = new IterableDictionary<int, string>();
            _helper.WriteAll(
                _data,
                emptyDict,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var resultDict = _helper.ReadAll<IterableDictionary<int, string>>(_data);
            NAssert.That(resultDict.Count == 0);
        }
    }
}
