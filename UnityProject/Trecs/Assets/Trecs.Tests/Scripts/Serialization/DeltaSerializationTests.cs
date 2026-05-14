using System;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class DeltaSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();

            // Register custom serializers for test classes
            _serializerRegistry.RegisterSerializer<Foo.Serializer>();
            _serializerRegistry.RegisterSerializerDelta<Foo.Serializer>();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deltaSize = _cacheHelper.MemoryStream.Position;

            // Write full object for comparison
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(foo, TestConstants.Version, includeTypeChecks: true, flags);
            var fullSize = _cacheHelper.MemoryStream.Position;

            // Read back delta
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta<Foo>(fooBase);

            // Assert
            TrecsAssert.That(result.A == foo.A);
            TrecsAssert.That(result.B == foo.B);
            TrecsAssert.That(result.C == foo.C);

            // Delta should be smaller than full serialization
            TrecsAssert.That(
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deltaSize = _cacheHelper.MemoryStream.Position;

            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta<Foo>(fooBase);

            // Assert
            TrecsAssert.That(result.A == foo.A);
            TrecsAssert.That(result.B == foo.B);
            TrecsAssert.That(result.C == foo.C);

            // No delta should result in minimal size (just headers)
            TrecsAssert.That(
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                foo,
                fooBase,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta<Foo>(fooBase);

            // Assert
            TrecsAssert.That(result.A == foo.A);
            TrecsAssert.That(result.B == foo.B);
            TrecsAssert.That(result.C == foo.C);
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
