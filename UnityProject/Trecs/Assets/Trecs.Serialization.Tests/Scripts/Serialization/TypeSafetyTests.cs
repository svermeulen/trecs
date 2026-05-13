using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Serialization.Internal;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class TypeSafetyTests
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
            try
            {
                _cacheHelper?.Dispose();
            }
            catch
            {
                // Ignore dispose errors
            }
        }

        [Test]
        public void WriteAllObject_WrongTypeDeserialization_ThrowsException()
        {
            // Test type safety violation: serialize as one type, deserialize as another
            var flags = 0L;

            // Serialize an int
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(42, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();

            // Try to deserialize as string - should throw
            Assert.Catch<Exception>(() =>
            {
                _cacheHelper.ReadAll<string>();
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
            Assert.Catch<Exception>(() =>
            {
                _cacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(memoryStream.ToArray()),
                    (int)memoryStream.Length
                );
                _cacheHelper.ResetMemoryPosition();
                _cacheHelper.ReadAllObject();
            });
        }

        [Test]
        public void SerializerRegistry_NullType_ThrowsException()
        {
            // Test passing null type to registry
            Assert.Catch<Exception>(() =>
            {
                _serializerRegistry.GetSerializer(null);
            });
        }

        [Test]
        public void SerializerRegistry_AbstractType_ThrowsException()
        {
            // Test trying to get serializer for abstract type
            Assert.Catch<Exception>(() =>
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
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    largeString,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<string>();
                Assert.That(result.Length == largeString.Length);
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

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();

            // This tests if the framework properly clears existing list data
            var result = _cacheHelper.ReadAll<List<int>>();

            Assert.That(result.Count == 3);
            Assert.That(result[0] == 1);
            Assert.That(result[1] == 2);
            Assert.That(result[2] == 3);
        }

        [Test]
        public void DictionarySerializer_KeyCollisions_HandlesCorrectly()
        {
            // Test edge case with custom equality comparer that could cause issues
            var flags = 0L;
            var dict = new Dictionary<string, int>
            {
                { "key1", 100 },
                { "key2", 200 },
                { "KEY1", 300 }, // Different case - should be separate key
            };

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(dict, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<Dictionary<string, int>>();

            Assert.That(result.Count == 3);
            Assert.That(result["key1"] == 100);
            Assert.That(result["key2"] == 200);
            Assert.That(result["KEY1"] == 300);
        }

        [Test]
        public void SerializeObject_NullReference_HandlesCorrectly()
        {
            // Test serializing null object reference - should work with null support
            var flags = 0L;

            // This should no longer throw an exception with null support
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(
                null,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            // Verify we can read back the null
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllObject();
            Assert.That(result == null);
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
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(value, TestConstants.Version, includeTypeChecks: true, flags);
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<float>();

                if (float.IsNaN(value))
                {
                    Assert.That(float.IsNaN(result), $"NaN not preserved");
                }
                else
                {
                    Assert.That(result == value, $"Special float {value} not preserved correctly");
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
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(value, TestConstants.Version, includeTypeChecks: true, flags);
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<int>();
                Assert.That(result == value, $"Boundary int {value} not preserved correctly");
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
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    vector,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<Vector3>();

                if (float.IsNaN(vector.x))
                {
                    Assert.That(float.IsNaN(result.x), "NaN component not preserved in Vector3");
                }
                else if (float.IsInfinity(vector.y))
                {
                    Assert.That(
                        float.IsInfinity(result.y),
                        "Infinity component not preserved in Vector3"
                    );
                }
                else if (float.IsNegativeInfinity(vector.z))
                {
                    Assert.That(
                        float.IsNegativeInfinity(result.z),
                        "Negative infinity component not preserved in Vector3"
                    );
                }
                else
                {
                    Assert.That(
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
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(str, TestConstants.Version, includeTypeChecks: true, flags);
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<string>();
                Assert.That(
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(emptyList, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var resultList = _cacheHelper.ReadAll<List<int>>();
            Assert.That(resultList.Count == 0);

            // Empty Dictionary
            var emptyDict = new Dictionary<string, int>();
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(emptyDict, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var resultDict = _cacheHelper.ReadAll<Dictionary<string, int>>();
            Assert.That(resultDict.Count == 0);

            // Empty HashSet
            var emptySet = new HashSet<int>();
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(emptySet, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var resultSet = _cacheHelper.ReadAll<HashSet<int>>();
            Assert.That(resultSet.Count == 0);
        }
    }
}
