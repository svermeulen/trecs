using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class UnsafeListSerializationTests
    {
        SerializerRegistry _serializerRegistry;
        SerializationHelper _helper;
        SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void EmptyList_RoundTrips()
        {
            var original = new UnsafeList<int>(0, Allocator.Persistent);
            try
            {
                var deserialized = RoundTrip(original);
                try
                {
                    NAssert.That(deserialized.IsCreated);
                    NAssert.That(deserialized.Length == 0);
                }
                finally
                {
                    deserialized.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }

        [Test]
        public void IntList_RoundTrips()
        {
            var original = new UnsafeList<int>(8, Allocator.Persistent);
            try
            {
                original.Add(1);
                original.Add(2);
                original.Add(3);
                original.Add(42);
                original.Add(-5);
                original.Add(int.MaxValue);
                original.Add(int.MinValue);

                var deserialized = RoundTrip(original);
                try
                {
                    NAssert.That(deserialized.IsCreated);
                    NAssert.That(deserialized.Length == original.Length);
                    for (int i = 0; i < original.Length; i++)
                    {
                        NAssert.That(deserialized[i] == original[i], $"Element {i} mismatch");
                    }
                }
                finally
                {
                    deserialized.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }

        [Test]
        public void FloatList_RoundTrips()
        {
            var original = new UnsafeList<float>(8, Allocator.Persistent);
            try
            {
                original.Add(1.0f);
                original.Add(-2.5f);
                original.Add(3.14159f);
                original.Add(0.0f);
                original.Add(float.MaxValue);
                original.Add(float.MinValue);

                var deserialized = RoundTrip(original);
                try
                {
                    NAssert.That(deserialized.Length == original.Length);
                    for (int i = 0; i < original.Length; i++)
                    {
                        NAssert.That(deserialized[i] == original[i], $"Element {i} mismatch");
                    }
                }
                finally
                {
                    deserialized.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }

        [Test]
        public void Vector3List_RoundTrips()
        {
            var original = new UnsafeList<Vector3>(4, Allocator.Persistent);
            try
            {
                original.Add(Vector3.zero);
                original.Add(Vector3.one);
                original.Add(new Vector3(1.5f, -2.3f, 42.7f));
                original.Add(new Vector3(float.MaxValue, float.MinValue, 0.0f));

                var deserialized = RoundTrip(original);
                try
                {
                    NAssert.That(deserialized.Length == original.Length);
                    for (int i = 0; i < original.Length; i++)
                    {
                        NAssert.That(deserialized[i] == original[i], $"Element {i} mismatch");
                    }
                }
                finally
                {
                    deserialized.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }

        [Test]
        public void LargeIntList_RoundTrips()
        {
            const int count = 10_000;
            var original = new UnsafeList<int>(count, Allocator.Persistent);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    original.Add(i * 7 + 13);
                }

                var deserialized = RoundTrip(original);
                try
                {
                    NAssert.That(deserialized.Length == count);
                    for (int i = 0; i < count; i++)
                    {
                        NAssert.That(deserialized[i] == original[i], $"Element {i} mismatch");
                    }
                }
                finally
                {
                    deserialized.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }

        [Test]
        public void Deserialize_InPlace_ReusesExistingList()
        {
            var original = new UnsafeList<int>(4, Allocator.Persistent);
            original.Add(10);
            original.Add(20);
            original.Add(30);

            var target = new UnsafeList<int>(0, Allocator.Persistent);
            try
            {
                _helper.WriteAll(
                    _data,
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags: 0L
                );

                // Capture IsCreated state up front — confirms the serializer
                // doesn't replace the live container with a fresh allocation
                // when one already exists.
                NAssert.That(target.IsCreated);

                _helper.ReadAll(_data, ref target);

                NAssert.That(target.IsCreated);
                NAssert.That(target.Length == original.Length);
                for (int i = 0; i < original.Length; i++)
                {
                    NAssert.That(target[i] == original[i], $"Element {i} mismatch");
                }
            }
            finally
            {
                target.Dispose();
                original.Dispose();
            }
        }

        UnsafeList<T> RoundTrip<T>(in UnsafeList<T> value)
            where T : unmanaged
        {
            _helper.WriteAll(
                _data,
                value,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L
            );
            return _helper.ReadAll<UnsafeList<T>>(_data);
        }
    }
}
