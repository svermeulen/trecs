using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using Trecs.Serialization;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Trecs.Tests
{
    [TestFixture]
    public class DenseDictionaryBlitSerializationTests
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
        public void DenseDictionary_UnmanagedTypes_UsesBlitPath()
        {
            // Verify that blit path is selected for unmanaged types
            TrecsAssert.That(
                DenseDictionaryBlitHelperCache<int, float>.CanUseBlit,
                "Blit path should be enabled for int/float dictionary"
            );
        }

        [Test]
        public void DenseDictionary_ManagedValueType_UsesFallbackPath()
        {
            // Verify that blit path is NOT selected when TValue is managed
            TrecsAssert.That(
                !DenseDictionaryBlitHelperCache<int, string>.CanUseBlit,
                "Blit path should be disabled for int/string dictionary"
            );
        }

        [Test]
        public void DenseDictionary_EmptyDictionary_SerializesAndDeserializes()
        {
            // Arrange
            var original = new DenseDictionary<int, float>();
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<DenseDictionary<int, float>>();

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == 0);
        }

        [Test]
        public void DenseDictionary_IntFloat_SerializesAndDeserializes()
        {
            // Arrange
            var original = new DenseDictionary<int, float>();
            original.Add(1, 1.5f);
            original.Add(2, 2.5f);
            original.Add(3, 3.5f);
            original.Add(-42, -42.42f);
            original.Add(int.MaxValue, float.MaxValue);
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<DenseDictionary<int, float>>();

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == original.Count);

            foreach (var kvp in original)
            {
                TrecsAssert.That(
                    deserialized.ContainsKey(kvp.Key),
                    $"Deserialized dictionary should contain key {kvp.Key}"
                );
                TrecsAssert.That(
                    deserialized[kvp.Key] == kvp.Value,
                    $"Value for key {kvp.Key} should match. Expected: {kvp.Value}, Actual: {deserialized[kvp.Key]}"
                );
            }
        }

        [Test]
        public void DenseDictionary_IntVector3_SerializesAndDeserializes()
        {
            // Arrange
            var original = new DenseDictionary<int, Vector3>();
            original.Add(1, Vector3.zero);
            original.Add(2, Vector3.one);
            original.Add(3, Vector3.up);
            original.Add(4, new Vector3(1.5f, -2.3f, 42.7f));
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<DenseDictionary<int, Vector3>>();

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == original.Count);

            foreach (var kvp in original)
            {
                TrecsAssert.That(deserialized.ContainsKey(kvp.Key));
                TrecsAssert.That(
                    deserialized[kvp.Key] == kvp.Value,
                    $"Vector3 for key {kvp.Key} should match"
                );
            }
        }

        [Test]
        public void DenseDictionary_Int2Float3_SerializesAndDeserializes()
        {
            // Arrange - Test with Unity.Mathematics types
            var original = new DenseDictionary<int2, float3>();
            original.Add(new int2(0, 0), new float3(1, 2, 3));
            original.Add(new int2(1, 2), new float3(4, 5, 6));
            original.Add(new int2(-1, -2), new float3(-7, -8, -9));
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<DenseDictionary<int2, float3>>();

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == original.Count);

            foreach (var kvp in original)
            {
                TrecsAssert.That(deserialized.ContainsKey(kvp.Key));
                TrecsAssert.That(
                    deserialized[kvp.Key].Equals(kvp.Value),
                    $"float3 for key {kvp.Key} should match"
                );
            }
        }

        [Test]
        public void DenseDictionary_LargeDictionary_SerializesAndDeserializes()
        {
            // Arrange
            var original = new DenseDictionary<int, int>();
            const int count = 10000;
            for (int i = 0; i < count; i++)
            {
                original.Add(i, i * 7 + 13);
            }
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<DenseDictionary<int, int>>();

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == count);

            // Check first, middle, and last elements
            TrecsAssert.That(deserialized[0] == original[0]);
            TrecsAssert.That(deserialized[5000] == original[5000]);
            TrecsAssert.That(deserialized[9999] == original[9999]);

            // Spot check
            for (int i = 0; i < 100; i++)
            {
                int key = i * 100;
                TrecsAssert.That(
                    deserialized[key] == original[key],
                    $"Value for key {key} should match"
                );
            }
        }

        [Test]
        public void DenseDictionary_SingleElement_SerializesAndDeserializes()
        {
            // Arrange
            var original = new DenseDictionary<int, int>();
            original.Add(42, 123);
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<DenseDictionary<int, int>>();

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == 1);
            TrecsAssert.That(deserialized[42] == 123);
        }

        [Test]
        public void DenseDictionary_PreInitialized_DeserializesCorrectly()
        {
            // Arrange - Test deserializing into an existing dictionary
            var original = new DenseDictionary<int, int>();
            original.Add(1, 100);
            original.Add(2, 200);
            original.Add(3, 300);
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();

            // Create a pre-existing empty dictionary
            var deserialized = new DenseDictionary<int, int>();

            // Act
            _cacheHelper.ReadAll(ref deserialized);

            // Assert
            TrecsAssert.IsNotNull(deserialized);
            TrecsAssert.That(deserialized.Count == original.Count);
            foreach (var kvp in original)
            {
                TrecsAssert.That(deserialized[kvp.Key] == kvp.Value);
            }
        }

        [Test]
        public void DenseDictionary_BlitPerformanceTest_CompareWithRegularDictionary()
        {
            // Arrange
            var svDict = new DenseDictionary<int, int>();
            var regularDict = new Dictionary<int, int>();

            const int elementCount = 1000;
            for (int i = 0; i < elementCount; i++)
            {
                int value = i * 42;
                svDict.Add(i, value);
                regularDict.Add(i, value);
            }

            var flags = 0L;

            // Act & Assert - Verify both serialize/deserialize correctly
            // DenseDictionary (blit path)
            _cacheHelper.ClearMemoryStream();
            var stopwatch = Stopwatch.StartNew();
            _cacheHelper.WriteAll(svDict, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserializedSvDict = _cacheHelper.ReadAll<DenseDictionary<int, int>>();
            stopwatch.Stop();
            var svDictTime = stopwatch.ElapsedTicks;

            // Regular Dictionary (element-by-element)
            _cacheHelper.ClearMemoryStream();
            stopwatch.Restart();
            _cacheHelper.WriteAll(
                regularDict,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedRegularDict = _cacheHelper.ReadAll<Dictionary<int, int>>();
            stopwatch.Stop();
            var regularDictTime = stopwatch.ElapsedTicks;

            // Verify correctness
            TrecsAssert.That(deserializedSvDict.Count == elementCount);
            TrecsAssert.That(deserializedRegularDict.Count == elementCount);
            for (int i = 0; i < elementCount; i++)
            {
                TrecsAssert.That(deserializedSvDict[i] == deserializedRegularDict[i]);
            }

            // Log performance comparison
            Debug.Log(
                $"DenseDictionary (blit): {svDictTime} ticks, "
                    + $"Regular Dictionary: {regularDictTime} ticks. "
                    + $"Ratio: {(float)svDictTime / regularDictTime:F2}x"
            );
        }
    }
}
