using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Tests
{
    /// <summary>
    /// Round-trip smoke tests for every serializer registered in
    /// <see cref="TestSerializerInstaller.CreateTestRegistry"/>.
    /// When adding a new serializer registration, add a corresponding test here.
    /// </summary>
    [TestFixture]
    public class SerializerRoundTripTests
    {
        SerializerRegistry _registry;
        SerializationBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            _registry = TestSerializerInstaller.CreateTestRegistry();
            _buffer = new SerializationBuffer(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
        }

        // ── Helpers ──────────────────────────────────────────────

        T RoundTrip<T>(in T value)
        {
            _buffer.ClearMemoryStream();
            _buffer.WriteAll(value, TestConstants.Version, includeTypeChecks: true, flags: 0L);
            _buffer.ResetMemoryPosition();
            return _buffer.ReadAll<T>();
        }

        static void AssertListEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count, "Count mismatch");
            for (int i = 0; i < expected.Count; i++)
                Assert.AreEqual(expected[i], actual[i], $"Mismatch at index {i}");
        }

        static void AssertIterableDictionaryEqual<TKey, TValue>(
            IterableDictionary<TKey, TValue> expected,
            IterableDictionary<TKey, TValue> actual
        )
            where TKey : struct, IEquatable<TKey>
        {
            Assert.AreEqual(expected.Count, actual.Count, "Count mismatch");
            foreach (var kvp in expected)
            {
                Assert.IsTrue(actual.ContainsKey(kvp.Key), $"Missing key: {kvp.Key}");
                Assert.AreEqual(kvp.Value, actual[kvp.Key], $"Value mismatch for key {kvp.Key}");
            }
        }

        static void AssertBlitListEqual<T>(List<T> expected, List<T> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count, "Count mismatch");
            for (int i = 0; i < expected.Count; i++)
                Assert.AreEqual(expected[i], actual[i], $"Mismatch at index {i}");
        }

        static void AssertUnsafeListEqual<T>(UnsafeList<T> expected, UnsafeList<T> actual)
            where T : unmanaged
        {
            Assert.AreEqual(expected.Length, actual.Length, "Length mismatch");
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], actual[i], $"Mismatch at index {i}");
        }

        // ── Blit primitives ─────────────────────────────────────

        [Test]
        public void Blit_Bool() => Assert.AreEqual(true, RoundTrip(true));

        [Test]
        public void Blit_Byte() => Assert.AreEqual((byte)42, RoundTrip((byte)42));

        [Test]
        public void Blit_SByte() => Assert.AreEqual((sbyte)-7, RoundTrip((sbyte)-7));

        [Test]
        public void Blit_Short() => Assert.AreEqual((short)-1234, RoundTrip((short)-1234));

        [Test]
        public void Blit_UShort() => Assert.AreEqual((ushort)1234, RoundTrip((ushort)1234));

        [Test]
        public void Blit_Int() => Assert.AreEqual(42, RoundTrip(42));

        [Test]
        public void Blit_UInt() => Assert.AreEqual(42u, RoundTrip(42u));

        [Test]
        public void Blit_Long() => Assert.AreEqual(123456789L, RoundTrip(123456789L));

        [Test]
        public void Blit_ULong() => Assert.AreEqual(123456789UL, RoundTrip(123456789UL));

        [Test]
        public void Blit_Float() => Assert.AreEqual(3.14f, RoundTrip(3.14f));

        [Test]
        public void Blit_Double() => Assert.AreEqual(3.14159, RoundTrip(3.14159));

        [Test]
        public void Blit_Decimal() => Assert.AreEqual(1.23m, RoundTrip(1.23m));

        // ── Math types ──────────────────────────────────────────

        [Test]
        public void Blit_Vector3() =>
            Assert.AreEqual(
                new Vector3(1.5f, -2.3f, 0.7f),
                RoundTrip(new Vector3(1.5f, -2.3f, 0.7f))
            );

        [Test]
        public void Blit_Vector4() =>
            Assert.AreEqual(new Vector4(1, 2, 3, 4), RoundTrip(new Vector4(1, 2, 3, 4)));

        [Test]
        public void Blit_Float2() =>
            Assert.AreEqual(new float2(1.5f, -2.3f), RoundTrip(new float2(1.5f, -2.3f)));

        [Test]
        public void Blit_Float3() =>
            Assert.AreEqual(new float3(1, 2, 3), RoundTrip(new float3(1, 2, 3)));

        [Test]
        public void Blit_Int2() => Assert.AreEqual(new int2(42, -7), RoundTrip(new int2(42, -7)));

        [Test]
        public void Blit_Quaternion() =>
            Assert.AreEqual(
                new quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                RoundTrip(new quaternion(0.1f, 0.2f, 0.3f, 0.9f))
            );

        // ── String ──────────────────────────────────────────────

        [Test]
        public void String_Value() => Assert.AreEqual("hello world", RoundTrip("hello world"));

        [Test]
        public void String_Empty() => Assert.AreEqual("", RoundTrip(""));

        // ── List ────────────────────────────────────────────────

        [Test]
        public void List_Int()
        {
            var original = new List<int> { 1, -5, 42, 0, int.MaxValue };
            AssertListEqual(original, RoundTrip(original));
        }

        [Test]
        public void List_String()
        {
            var original = new List<string> { "hello", "world", "" };
            AssertListEqual(original, RoundTrip(original));
        }

        [Test]
        public void List_Nested()
        {
            var original = new List<List<int>>
            {
                new List<int> { 1, 2, 3 },
                new List<int> { 4, 5 },
                new List<int>(),
            };
            var result = RoundTrip(original);
            Assert.AreEqual(original.Count, result.Count);
            for (int i = 0; i < original.Count; i++)
                AssertListEqual(original[i], result[i]);
        }

        // ── Array ───────────────────────────────────────────────

        [Test]
        public void Array_BlitInt()
        {
            var original = new[] { 1, 2, 3, -42 };
            CollectionAssert.AreEqual(original, RoundTrip(original));
        }

        [Test]
        public void Array_String()
        {
            var original = new[] { "hello", "world", "" };
            CollectionAssert.AreEqual(original, RoundTrip(original));
        }

        // ── Queue ───────────────────────────────────────────────

        [Test]
        public void Queue_Int()
        {
            var original = new Queue<int>();
            original.Enqueue(1);
            original.Enqueue(2);
            original.Enqueue(3);
            CollectionAssert.AreEqual(original.ToArray(), RoundTrip(original).ToArray());
        }

        // ── List (blit) ────────────────────────────────────────

        [Test]
        public void ListBlit_Int()
        {
            AssertBlitListEqual(
                new List<int> { 1, -5, 42, 0 },
                RoundTrip(new List<int> { 1, -5, 42, 0 })
            );
        }

        [Test]
        public void ListBlit_Float()
        {
            AssertBlitListEqual(
                new List<float> { 1.5f, -2.3f, 0f },
                RoundTrip(new List<float> { 1.5f, -2.3f, 0f })
            );
        }

        [Test]
        public void ListBlit_Vector3()
        {
            AssertBlitListEqual(
                new List<Vector3> { Vector3.zero, Vector3.one, new Vector3(1, 2, 3) },
                RoundTrip(new List<Vector3> { Vector3.zero, Vector3.one, new Vector3(1, 2, 3) })
            );
        }

        [Test]
        public void ListBlit_Int2()
        {
            AssertBlitListEqual(
                new List<int2> { new int2(1, 2), new int2(-3, 4) },
                RoundTrip(new List<int2> { new int2(1, 2), new int2(-3, 4) })
            );
        }

        [Test]
        public void ListBlit_Byte()
        {
            AssertBlitListEqual(
                new List<byte> { 0, 1, 127, 255 },
                RoundTrip(new List<byte> { 0, 1, 127, 255 })
            );
        }

        // ── IterableDictionary (unmanaged — blit path) ─────────────

        [Test]
        public void IterableDictionary_IntFloat()
        {
            var original = new IterableDictionary<int, float>();
            original.Add(1, 1.5f);
            original.Add(2, -2.3f);
            original.Add(3, 0f);
            AssertIterableDictionaryEqual(original, RoundTrip(original));
        }

        [Test]
        public void IterableDictionary_IntInt()
        {
            var original = new IterableDictionary<int, int>();
            original.Add(1, 10);
            original.Add(-42, 99);
            AssertIterableDictionaryEqual(original, RoundTrip(original));
        }

        [Test]
        public void IterableDictionary_IntVector3()
        {
            var original = new IterableDictionary<int, Vector3>();
            original.Add(1, Vector3.one);
            original.Add(2, new Vector3(1, 2, 3));
            AssertIterableDictionaryEqual(original, RoundTrip(original));
        }

        [Test]
        public void IterableDictionary_Int2Float3()
        {
            var original = new IterableDictionary<int2, float3>();
            original.Add(new int2(0, 0), new float3(1, 2, 3));
            original.Add(new int2(1, 2), new float3(4, 5, 6));
            AssertIterableDictionaryEqual(original, RoundTrip(original));
        }

        // ── IterableDictionary (managed — element-by-element path) ─

        [Test]
        public void IterableDictionary_IntString()
        {
            var original = new IterableDictionary<int, string>();
            original.Add(1, "hello");
            original.Add(2, "world");
            original.Add(3, "");
            AssertIterableDictionaryEqual(original, RoundTrip(original));
        }

        // ── UnsafeList ──────────────────────────────────────────

        [Test]
        public void UnsafeList_Float()
        {
            var original = new UnsafeList<float>(4, Allocator.Persistent);
            try
            {
                original.Add(1.5f);
                original.Add(-2.3f);
                original.Add(0f);
                var result = RoundTrip(original);
                try
                {
                    AssertUnsafeListEqual(original, result);
                }
                finally
                {
                    result.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }

        [Test]
        public void UnsafeList_Vector3()
        {
            var original = new UnsafeList<Vector3>(4, Allocator.Persistent);
            try
            {
                original.Add(Vector3.zero);
                original.Add(Vector3.one);
                var result = RoundTrip(original);
                try
                {
                    AssertUnsafeListEqual(original, result);
                }
                finally
                {
                    result.Dispose();
                }
            }
            finally
            {
                original.Dispose();
            }
        }
    }
}
