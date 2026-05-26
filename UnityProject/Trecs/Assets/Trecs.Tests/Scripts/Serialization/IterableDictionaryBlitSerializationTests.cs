using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Tests
{
    [TestFixture]
    public class IterableDictionaryBlitSerializationTests
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
        public void IterableDictionary_EmptyDictionary_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableDictionary<int, float>();
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<IterableDictionary<int, float>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == 0);
        }

        [Test]
        public void IterableDictionary_IntFloat_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableDictionary<int, float>();
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
            var deserialized = _cacheHelper.ReadAll<IterableDictionary<int, float>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);

            foreach (var kvp in original)
            {
                TrecsDebugAssert.That(
                    deserialized.ContainsKey(kvp.Key),
                    $"Deserialized dictionary should contain key {kvp.Key}"
                );
                TrecsDebugAssert.That(
                    deserialized[kvp.Key] == kvp.Value,
                    $"Value for key {kvp.Key} should match. Expected: {kvp.Value}, Actual: {deserialized[kvp.Key]}"
                );
            }
        }

        [Test]
        public void IterableDictionary_IntVector3_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableDictionary<int, Vector3>();
            original.Add(1, Vector3.zero);
            original.Add(2, Vector3.one);
            original.Add(3, Vector3.up);
            original.Add(4, new Vector3(1.5f, -2.3f, 42.7f));
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<IterableDictionary<int, Vector3>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);

            foreach (var kvp in original)
            {
                TrecsDebugAssert.That(deserialized.ContainsKey(kvp.Key));
                TrecsDebugAssert.That(
                    deserialized[kvp.Key] == kvp.Value,
                    $"Vector3 for key {kvp.Key} should match"
                );
            }
        }

        [Test]
        public void IterableDictionary_Int2Float3_SerializesAndDeserializes()
        {
            // Arrange - Test with Unity.Mathematics types
            var original = new IterableDictionary<int2, float3>();
            original.Add(new int2(0, 0), new float3(1, 2, 3));
            original.Add(new int2(1, 2), new float3(4, 5, 6));
            original.Add(new int2(-1, -2), new float3(-7, -8, -9));
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<IterableDictionary<int2, float3>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);

            foreach (var kvp in original)
            {
                TrecsDebugAssert.That(deserialized.ContainsKey(kvp.Key));
                TrecsDebugAssert.That(
                    deserialized[kvp.Key].Equals(kvp.Value),
                    $"float3 for key {kvp.Key} should match"
                );
            }
        }

        [Test]
        public void IterableDictionary_LargeDictionary_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableDictionary<int, int>();
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
            var deserialized = _cacheHelper.ReadAll<IterableDictionary<int, int>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == count);

            // Check first, middle, and last elements
            TrecsDebugAssert.That(deserialized[0] == original[0]);
            TrecsDebugAssert.That(deserialized[5000] == original[5000]);
            TrecsDebugAssert.That(deserialized[9999] == original[9999]);

            // Spot check
            for (int i = 0; i < 100; i++)
            {
                int key = i * 100;
                TrecsDebugAssert.That(
                    deserialized[key] == original[key],
                    $"Value for key {key} should match"
                );
            }
        }

        [Test]
        public void IterableDictionary_SingleElement_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableDictionary<int, int>();
            original.Add(42, 123);
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserialized = _cacheHelper.ReadAll<IterableDictionary<int, int>>();

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == 1);
            TrecsDebugAssert.That(deserialized[42] == 123);
        }

        [Test]
        public void IterableDictionary_PreInitialized_DeserializesCorrectly()
        {
            // Arrange - Test deserializing into an existing dictionary
            var original = new IterableDictionary<int, int>();
            original.Add(1, 100);
            original.Add(2, 200);
            original.Add(3, 300);
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();

            // Create a pre-existing empty dictionary
            var deserialized = new IterableDictionary<int, int>();

            // Act
            _cacheHelper.ReadAll(ref deserialized);

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);
            foreach (var kvp in original)
            {
                TrecsDebugAssert.That(deserialized[kvp.Key] == kvp.Value);
            }
        }
    }
}
