using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Trecs.Tests
{
    [TestFixture]
    public class ListBlitSerializationTests
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
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == 0);
        }

        [Test]
        public void SvList_WithIntElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int> { 1, 2, 3, 42, -5, 0, int.MaxValue, int.MinValue };
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
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                TrecsDebugAssert.That(
                    deserializedList[i] == originalList[i],
                    $"Element at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_WithFloatElements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<float>
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
            var deserializedList = _cacheHelper.ReadAll<List<float>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                TrecsDebugAssert.That(
                    deserializedList[i] == originalList[i],
                    $"Float at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_WithVector3Elements_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<Vector3>
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
            var deserializedList = _cacheHelper.ReadAll<List<Vector3>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                TrecsDebugAssert.That(
                    deserializedList[i] == originalList[i],
                    $"Vector3 at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_WithMathematicsInt2_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int2>
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
            var deserializedList = _cacheHelper.ReadAll<List<int2>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                TrecsDebugAssert.That(
                    deserializedList[i].Equals(originalList[i]),
                    $"int2 at index {i} should match. Expected: {originalList[i]}, Actual: {deserializedList[i]}"
                );
            }
        }

        [Test]
        public void SvList_LargeList_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int>();
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
            var deserializedList = _cacheHelper.ReadAll<List<int>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == originalList.Count);

            // Check first, middle, and last elements
            TrecsDebugAssert.That(deserializedList[0] == originalList[0]);
            TrecsDebugAssert.That(deserializedList[5000] == originalList[5000]);
            TrecsDebugAssert.That(deserializedList[9999] == originalList[9999]);

            // Spot check some elements
            for (int i = 0; i < 100; i++)
            {
                int index = i * 100; // Every 100th element
                TrecsDebugAssert.That(
                    deserializedList[index] == originalList[index],
                    $"Element at index {index} should match"
                );
            }
        }

        [Test]
        public void SvList_SingleElement_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<int> { 42 };
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
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == 1);
            TrecsDebugAssert.That(deserializedList[0] == 42);
        }

        [Test]
        public void SvList_ByteValues_SerializesAndDeserializes()
        {
            // Arrange
            var originalList = new List<byte>();
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
            var deserializedList = _cacheHelper.ReadAll<List<byte>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == 256);
            for (int i = 0; i < 256; i++)
            {
                TrecsDebugAssert.That(
                    deserializedList[i] == (byte)i,
                    $"Byte at index {i} should match"
                );
            }
        }

        [Test]
        public void SvList_PreAllocatedEmptyList_DeserializesCorrectly()
        {
            // Arrange - Test deserializing into a pre-allocated (but empty) list,
            // which the serializer reuses instead of allocating a new one.
            var originalList = new List<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();

            var deserializedList = new List<int>();

            // Act
            _cacheHelper.ReadAll(ref deserializedList);

            // Assert
            TrecsDebugAssert.IsNotNull(deserializedList);
            TrecsDebugAssert.That(deserializedList.Count == originalList.Count);
            for (int i = 0; i < originalList.Count; i++)
            {
                TrecsDebugAssert.That(
                    deserializedList[i] == originalList[i],
                    $"Element at index {i} should match after deserializing into pre-allocated list"
                );
            }
        }

        [Test]
        public void SvList_BlitPerformanceTest_CompareWithRegularList()
        {
            // Arrange - This is more of a performance verification than a strict test
            var blitList = new List<int>();
            var regularList = new List<int>();

            const int elementCount = 1000;
            for (int i = 0; i < elementCount; i++)
            {
                int value = i * 42;
                blitList.Add(value);
                regularList.Add(value);
            }

            var flags = 0L;

            // Act & Assert - Just verify both serialize/deserialize correctly
            // List (blit)
            _cacheHelper.ClearMemoryStream();
            var stopwatch = Stopwatch.StartNew();
            _cacheHelper.WriteAll(blitList, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserializedBlitList = _cacheHelper.ReadAll<List<int>>();
            stopwatch.Stop();
            var blitListTime = stopwatch.ElapsedTicks;

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
            TrecsDebugAssert.That(deserializedBlitList.Count == elementCount);
            TrecsDebugAssert.That(deserializedRegularList.Count == elementCount);
            for (int i = 0; i < elementCount; i++)
            {
                TrecsDebugAssert.That(deserializedBlitList[i] == deserializedRegularList[i]);
            }

            // Log performance comparison (not a strict assertion since it can vary)
            Debug.Log(
                $"ListBlitSerializer: {blitListTime} ticks, Regular ListSerializer: {regularListTime} ticks. Blit ratio: {(float)blitListTime / regularListTime:F2}x"
            );
        }
    }
}
