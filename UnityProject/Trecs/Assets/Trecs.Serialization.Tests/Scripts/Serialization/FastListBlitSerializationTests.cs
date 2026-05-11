using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Trecs.Collections;
using Unity.Mathematics;
using UnityEngine;
using Assert = Trecs.Internal.Assert;
using Debug = UnityEngine.Debug;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class FastListBlitSerializationTests
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
        public void SvList_EmptyList_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<int>();
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
            var deserializedList = _cacheHelper.ReadAll<FastList<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == 0);
        }

        [Test]
        public void SvList_WithIntElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<int> { 1, 2, 3, 42, -5, 0, int.MaxValue, int.MinValue };
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
            var deserializedList = _cacheHelper.ReadAll<FastList<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
                    deserializedList[i] == originalList[i],
                    $"Element at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_WithFloatElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<float>
            {
                1.0f,
                -2.5f,
                3.14159f,
                0.0f,
                float.MaxValue,
                float.MinValue,
                float.PositiveInfinity,
                float.NegativeInfinity,
            };
            // Note: NaN would not equal itself, so we skip it
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
            var deserializedList = _cacheHelper.ReadAll<FastList<float>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
                    deserializedList[i] == originalList[i],
                    $"Float at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_WithVector3Elements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<Vector3>
            {
                Vector3.zero,
                Vector3.one,
                Vector3.up,
                Vector3.right,
                Vector3.forward,
                new Vector3(1.5f, -2.3f, 42.7f),
                new Vector3(float.MaxValue, float.MinValue, 0.0f),
            };
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
            var deserializedList = _cacheHelper.ReadAll<FastList<Vector3>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
                    deserializedList[i] == originalList[i],
                    $"Vector3 at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_WithMathematicsInt2_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<int2>
            {
                new int2(0, 0),
                new int2(1, 1),
                new int2(-1, 2),
                new int2(int.MaxValue, int.MinValue),
                new int2(42, -42),
            };
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
            var deserializedList = _cacheHelper.ReadAll<FastList<int2>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
                    deserializedList[i].Equals(originalList[i]),
                    $"int2 at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_LargeList_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<int>();
            for (int i = 0; i < 10000; i++)
            {
                originalList.Add(i * 7 + 13); // Some pseudo-random values
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
            var deserializedList = _cacheHelper.ReadAll<FastList<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);

            // Check first, middle, and last elements
            Assert.That(deserializedList[0] == originalList[0]);
            Assert.That(deserializedList[5000] == originalList[5000]);
            Assert.That(deserializedList[9999] == originalList[9999]);

            // Spot check some elements
            for (int i = 0; i < 100; i++)
            {
                int index = i * 100; // Every 100th element
                Assert.That(
                    deserializedList[index] == originalList[index],
                    $"Element at index {index} should match"
                );
            }
        }

        [Test]
        public void SvList_SingleElement_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<int> { 42 };
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
            var deserializedList = _cacheHelper.ReadAll<FastList<int>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == 1);
            Assert.That(deserializedList[0] == 42);
        }

        [Test]
        public void SvList_ByteValues_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new FastList<byte>();
            for (byte i = 0; i < 255; i++)
            {
                originalList.Add(i);
            }
            originalList.Add(255); // Add max byte value
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
            var deserializedList = _cacheHelper.ReadAll<FastList<byte>>();

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == 256);
            for (int i = 0; i < 256; i++)
            {
                Assert.That(deserializedList[i] == (byte)i, $"Byte at index {i} should match");
            }
        }

        [Test]
        public void SvList_PreInitializedList_DeserializesCorrectly()
        {
            // Arrange - Test deserializing into an existing list that gets cleared
            var originalList = new FastList<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();

            // Create a pre-existing list with different data
            var deserializedList = new FastList<int> { 99, 88, 77 };

            // Act
            _cacheHelper.ReadAll(ref deserializedList);

            // Assert
            Assert.IsNotNull(deserializedList);
            Assert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                Assert.That(
                    deserializedList[i] == originalList[i],
                    $"Element at index {i} should match after deserializing into existing list"
                );
            }
        }

        [Test]
        public void SvList_BlitPerformanceTest_CompareWithRegularList()
        {
            // Arrange - This is more of a performance verification than a strict test
            var svList = new FastList<int>();
            var regularList = new List<int>();

            const int elementCount = 1000;
            for (int i = 0; i < elementCount; i++)
            {
                int value = i * 42;
                svList.Add(value);
                regularList.Add(value);
            }

            var flags = 0L;

            // Act & Assert - Just verify both serialize/deserialize correctly
            // FastList (blit)
            _cacheHelper.ClearMemoryStream();
            var stopwatch = Stopwatch.StartNew();
            _cacheHelper.WriteAll(svList, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserializedSvList = _cacheHelper.ReadAll<FastList<int>>();
            stopwatch.Stop();
            var svListTime = stopwatch.ElapsedTicks;

            // Regular List (element-by-element)
            _cacheHelper.ClearMemoryStream();
            stopwatch.Restart();
            _cacheHelper.WriteAll(
                regularList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedRegularList = _cacheHelper.ReadAll<List<int>>();
            stopwatch.Stop();
            var regularListTime = stopwatch.ElapsedTicks;

            // Verify correctness
            Assert.That(deserializedSvList.Count == elementCount);
            Assert.That(deserializedRegularList.Count == elementCount);
            for (int i = 0; i < elementCount; i++)
            {
                Assert.That(deserializedSvList[i] == deserializedRegularList[i]);
            }

            // Log performance comparison (not a strict assertion since it can vary)
            Debug.Log(
                $"FastListSerializer: {svListTime} ticks, Regular ListSerializer: {regularListTime} ticks. Blit ratio: {(float)svListTime / regularListTime:F2}x"
            );
        }
    }
}
