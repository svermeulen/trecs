using System;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class DeltaSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);

            // Register custom serializers for test classes
            _serializerRegistry.RegisterSerializer(new Foo.Serializer());
            _serializerRegistry.RegisterSerializerDelta<Foo.Serializer>();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void TestBasicDelta()
        {
            // Arrange
            var fooBase = new Foo
            {
                A = 1,
                B = 2,
                C = 0,
            };
            var foo = new Foo
            {
                A = 1,
                B = 2,
                C = 3,
            };
            var flags = 0L;

            // Act - Test delta serialization
            _helper.WriteAllDelta(
                _data,
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deltaSize = _data.ContiguousSize;

            // Write full object for comparison
            _helper.WriteAll(_data, foo, TestConstants.Version, includeTypeChecks: true, flags);
            var fullSize = _data.ContiguousSize;

            // Read back delta
            _helper.WriteAllDelta(
                _data,
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAllDelta<Foo>(_data, fooBase);

            // Assert
            TrecsDebugAssert.That(result.A == foo.A);
            TrecsDebugAssert.That(result.B == foo.B);
            TrecsDebugAssert.That(result.C == foo.C);

            // Delta should be smaller than full serialization
            TrecsDebugAssert.That(
                deltaSize < fullSize,
                $"Delta size ({deltaSize}) should be less than full size ({fullSize})"
            );
        }

        [Test]
        public void TestNoDelta()
        {
            // Arrange - identical objects
            var fooBase = new Foo
            {
                A = 1,
                B = 2,
                C = 3,
            };
            var foo = new Foo
            {
                A = 1,
                B = 2,
                C = 3,
            };
            var flags = 0L;

            // Act
            _helper.WriteAllDelta(
                _data,
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deltaSize = _data.ContiguousSize;

            var result = _helper.ReadAllDelta<Foo>(_data, fooBase);

            // Assert
            TrecsDebugAssert.That(result.A == foo.A);
            TrecsDebugAssert.That(result.B == foo.B);
            TrecsDebugAssert.That(result.C == foo.C);

            // No delta should result in minimal size (just headers)
            TrecsDebugAssert.That(
                deltaSize < 35,
                $"No-change delta should be minimal, but was {deltaSize} bytes"
            );
        }

        [Test]
        public void TestAllFieldsChanged()
        {
            // Arrange
            var fooBase = new Foo
            {
                A = 1,
                B = 2,
                C = 3,
            };
            var foo = new Foo
            {
                A = 4,
                B = 5,
                C = 6,
            };
            var flags = 0L;

            // Act
            _helper.WriteAllDelta(
                _data,
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAllDelta<Foo>(_data, fooBase);

            // Assert
            TrecsDebugAssert.That(result.A == foo.A);
            TrecsDebugAssert.That(result.B == foo.B);
            TrecsDebugAssert.That(result.C == foo.C);
        }

        // Test classes
        private class Foo : IEquatable<Foo>
        {
            public int A { get; set; }
            public int B { get; set; }
            public int C { get; set; }

            public bool Equals(Foo other)
            {
                if (other == null)
                    return false;
                return A == other.A && B == other.B && C == other.C;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as Foo);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(A, B, C);
            }

            public class Serializer : ISerializer<Foo>, ISerializerDelta<Foo>
            {
                public Serializer() { }

                public void Serialize(in Foo value, ISerializationWriter writer)
                {
                    writer.Write("A", value.A);
                    writer.Write("B", value.B);
                    writer.Write("C", value.C);
                }

                public void Deserialize(ref Foo value, ISerializationReader reader)
                {
                    value ??= new Foo();
                    value.A = reader.Read<int>("A");
                    value.B = reader.Read<int>("B");
                    value.C = reader.Read<int>("C");
                }

                public void SerializeDelta(
                    in Foo value,
                    in Foo baseValue,
                    ISerializationWriter writer
                )
                {
                    writer.BlitWriteDelta("A", value.A, baseValue.A);
                    writer.BlitWriteDelta("B", value.B, baseValue.B);
                    writer.BlitWriteDelta("C", value.C, baseValue.C);
                }

                public void DeserializeDelta(
                    ref Foo value,
                    in Foo baseValue,
                    ISerializationReader reader
                )
                {
                    value ??= new Foo();
                    var a = value.A;
                    var b = value.B;
                    var c = value.C;
                    reader.BlitReadDelta("A", ref a, baseValue.A);
                    reader.BlitReadDelta("B", ref b, baseValue.B);
                    reader.BlitReadDelta("C", ref c, baseValue.C);
                    value.A = a;
                    value.B = b;
                    value.C = c;
                }
            }
        }
    }
}
