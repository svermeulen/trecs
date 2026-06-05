using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs.Tests
{
    [TestFixture]
    public class IterableHashSetBlitSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void IterableHashSet_EmptySet_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableHashSet<int>();
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserialized = _helper.ReadAll<IterableHashSet<int>>(_data);

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == 0);
        }

        [Test]
        public void IterableHashSet_Ints_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableHashSet<int>();
            original.Add(1);
            original.Add(2);
            original.Add(3);
            original.Add(-42);
            original.Add(int.MaxValue);
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserialized = _helper.ReadAll<IterableHashSet<int>>(_data);

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);

            foreach (var item in original)
            {
                TrecsDebugAssert.That(
                    deserialized.Contains(item),
                    $"Deserialized set should contain {item}"
                );
            }
        }

        [Test]
        public void IterableHashSet_Int2_SerializesAndDeserializes()
        {
            // Arrange - multi-field unmanaged element
            var original = new IterableHashSet<int2>();
            original.Add(new int2(0, 0));
            original.Add(new int2(1, 2));
            original.Add(new int2(-1, -2));
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserialized = _helper.ReadAll<IterableHashSet<int2>>(_data);

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);

            foreach (var item in original)
            {
                TrecsDebugAssert.That(deserialized.Contains(item));
            }
        }

        [Test]
        public void IterableHashSet_LargeSet_SerializesAndDeserializes()
        {
            // Arrange
            var original = new IterableHashSet<int>();
            const int count = 10000;
            for (int i = 0; i < count; i++)
            {
                original.Add(i * 7 + 13);
            }
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserialized = _helper.ReadAll<IterableHashSet<int>>(_data);

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == count);

            // Spot check across the range, including post-deserialize lookups
            // (exercises the blitted bucket structure, not just the dense array).
            for (int i = 0; i < count; i += 97)
            {
                TrecsDebugAssert.That(
                    deserialized.Contains(i * 7 + 13),
                    $"Deserialized set should contain {i * 7 + 13}"
                );
            }
            TrecsDebugAssert.That(!deserialized.Contains(12));
        }

        [Test]
        public void IterableHashSet_PreInitialized_DeserializesCorrectly()
        {
            // Arrange - deserialize into an existing (cleared) instance, the
            // ReadInPlace shape SnapshotMetadata uses for its BlobIds set.
            var original = new IterableHashSet<int>();
            original.Add(100);
            original.Add(200);
            original.Add(300);
            var flags = 0L;

            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            var deserialized = new IterableHashSet<int>();

            // Act
            _helper.ReadAll(_data, ref deserialized);

            // Assert
            TrecsDebugAssert.IsNotNull(deserialized);
            TrecsDebugAssert.That(deserialized.Count == original.Count);
            foreach (var item in original)
            {
                TrecsDebugAssert.That(deserialized.Contains(item));
            }
        }

        [Test]
        public void IterableHashSet_ReusedAcrossRoundTrips_StaysConsistent()
        {
            // Arrange - reuse one instance across clear/refill/round-trip
            // cycles, mirroring the per-save reuse on the snapshot hot path.
            var original = new IterableHashSet<int>();
            var deserialized = new IterableHashSet<int>();
            var flags = 0L;

            for (int round = 0; round < 3; round++)
            {
                original.Clear();
                int count = 50 * (round + 1);
                for (int i = 0; i < count; i++)
                {
                    original.Add(i + round * 1000);
                }

                _data.Clear();
                _helper.WriteAll(
                    _data,
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                deserialized.Clear();
                _helper.ReadAll(_data, ref deserialized);

                // Assert per round
                TrecsDebugAssert.That(deserialized.Count == original.Count);
                foreach (var item in original)
                {
                    TrecsDebugAssert.That(deserialized.Contains(item));
                }
            }
        }

        [Test]
        public void IterableHashSet_IterationOrder_RoundTripsExactly()
        {
            // Arrange - the wire form must preserve the deterministic dense
            // iteration order (snapshot checksums depend on it).
            var original = new IterableHashSet<int>();
            for (int i = 0; i < 500; i++)
            {
                original.Add(i * 31 + 7);
            }
            // Punch some holes so the dense array isn't trivially sequential.
            original.TryRemove(7);
            original.TryRemove(317);
            original.Add(123456);
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserialized = _helper.ReadAll<IterableHashSet<int>>(_data);

            // Assert - identical iteration sequence
            var expected = new List<int>();
            foreach (var item in original)
            {
                expected.Add(item);
            }
            int index = 0;
            foreach (var item in deserialized)
            {
                TrecsDebugAssert.That(
                    item == expected[index],
                    $"Iteration order diverged at index {index}: expected {expected[index]}, got {item}"
                );
                index++;
            }
            TrecsDebugAssert.That(index == expected.Count);
        }
    }
}
