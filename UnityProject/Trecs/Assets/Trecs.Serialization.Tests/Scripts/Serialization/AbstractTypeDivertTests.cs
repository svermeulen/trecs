using System;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
{
    // Locks in the wire format for the abstract-T divert in
    // BinarySerializationWriter.Write<T>/WriteDelta<T> and BinarySerializationReader.
    // Read<T>/ReadDelta<T>: when T is abstract, the typed entry points forward
    // to the WriteObject/ReadObject family so the runtime concrete-type id is
    // emitted into the payload. Roundtripping via the typed entry points must
    // produce the correct concrete subtype.
    [TestFixture]
    public class AbstractTypeDivertTests
    {
        SerializerRegistry _serializerRegistry;
        SerializationBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();

            _serializerRegistry.RegisterSerializer<DerivedASerializer>();
            _serializerRegistry.RegisterSerializer<DerivedBSerializer>();
            _serializerRegistry.RegisterSerializerDelta<DerivedASerializer>();
            _serializerRegistry.RegisterSerializerDelta<DerivedBSerializer>();

            _buffer = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
        }

        [Test]
        public void Write_TAbstract_RoundtripsConcreteSubtype()
        {
            AbstractBase original = new DerivedA { Common = 42, AOnly = "alpha" };

            _buffer.ClearMemoryStream();
            _buffer.WriteAll<AbstractBase>(
                original,
                TestConstants.Version,
                includeTypeChecks: true
            );
            _buffer.ResetMemoryPosition();
            var result = _buffer.ReadAll<AbstractBase>();

            Assert.IsNotNull(result);
            Assert.IsInstanceOf<DerivedA>(result);
            var derived = (DerivedA)result;
            Assert.That(derived.Common == 42);
            Assert.That(derived.AOnly == "alpha");
        }

        [Test]
        public void Write_TAbstract_DistinguishesSubtypesByRuntimeType()
        {
            AbstractBase a = new DerivedA { Common = 1, AOnly = "x" };

            _buffer.ClearMemoryStream();
            _buffer.WriteAll<AbstractBase>(a, TestConstants.Version, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var resultA = _buffer.ReadAll<AbstractBase>();

            AbstractBase b = new DerivedB { Common = 2, BOnly = 7 };

            _buffer.ClearMemoryStream();
            _buffer.WriteAll<AbstractBase>(b, TestConstants.Version, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var resultB = _buffer.ReadAll<AbstractBase>();

            Assert.IsInstanceOf<DerivedA>(resultA);
            Assert.IsInstanceOf<DerivedB>(resultB);
            Assert.That(((DerivedA)resultA).AOnly == "x");
            Assert.That(((DerivedB)resultB).BOnly == 7);
        }

        [Test]
        public void Write_TAbstract_RoundtripsWithoutIncludeTypeChecks()
        {
            // The divert always emits the type id (via WriteObject) regardless
            // of includeTypeChecks, so reading must still succeed when the
            // toggle is off.
            AbstractBase original = new DerivedB { Common = 9, BOnly = 99 };

            _buffer.ClearMemoryStream();
            _buffer.WriteAll<AbstractBase>(
                original,
                TestConstants.Version,
                includeTypeChecks: false
            );
            _buffer.ResetMemoryPosition();
            var result = _buffer.ReadAll<AbstractBase>();

            Assert.IsInstanceOf<DerivedB>(result);
            var derived = (DerivedB)result;
            Assert.That(derived.Common == 9);
            Assert.That(derived.BOnly == 99);
        }

        [Test]
        public void WriteDelta_TAbstract_RoundtripsChangedConcreteSubtype()
        {
            AbstractBase baseValue = new DerivedA { Common = 1, AOnly = "old" };
            AbstractBase changed = new DerivedA { Common = 2, AOnly = "new" };

            _buffer.ClearMemoryStream();
            _buffer.WriteAllDelta<AbstractBase>(
                changed,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true
            );
            _buffer.ResetMemoryPosition();
            var result = _buffer.ReadAllDelta<AbstractBase>(baseValue);

            Assert.IsInstanceOf<DerivedA>(result);
            var derived = (DerivedA)result;
            Assert.That(derived.Common == 2);
            Assert.That(derived.AOnly == "new");
        }

        [Test]
        public void WriteDelta_TAbstract_RoundtripsUnchangedConcreteSubtype()
        {
            AbstractBase baseValue = new DerivedB { Common = 5, BOnly = 50 };
            AbstractBase same = new DerivedB { Common = 5, BOnly = 50 };

            _buffer.ClearMemoryStream();
            _buffer.WriteAllDelta<AbstractBase>(
                same,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true
            );
            _buffer.ResetMemoryPosition();
            var result = _buffer.ReadAllDelta<AbstractBase>(baseValue);

            Assert.IsInstanceOf<DerivedB>(result);
            var derived = (DerivedB)result;
            Assert.That(derived.Common == 5);
            Assert.That(derived.BOnly == 50);
        }

        public abstract class AbstractBase
        {
            public int Common { get; set; }
        }

        public class DerivedA : AbstractBase, IEquatable<DerivedA>
        {
            public string AOnly { get; set; }

            public bool Equals(DerivedA other) =>
                other != null && Common == other.Common && AOnly == other.AOnly;

            public override bool Equals(object obj) => Equals(obj as DerivedA);

            public override int GetHashCode() => HashCode.Combine(Common, AOnly);
        }

        public class DerivedB : AbstractBase, IEquatable<DerivedB>
        {
            public int BOnly { get; set; }

            public bool Equals(DerivedB other) =>
                other != null && Common == other.Common && BOnly == other.BOnly;

            public override bool Equals(object obj) => Equals(obj as DerivedB);

            public override int GetHashCode() => HashCode.Combine(Common, BOnly);
        }

        public class DerivedASerializer : ISerializer<DerivedA>, ISerializerDelta<DerivedA>
        {
            public DerivedASerializer() { }

            public void Serialize(in DerivedA value, ISerializationWriter writer)
            {
                writer.Write("Common", value.Common);
                writer.WriteString("AOnly", value.AOnly);
            }

            public void Deserialize(ref DerivedA value, ISerializationReader reader)
            {
                value ??= new DerivedA();
                value.Common = reader.Read<int>("Common");
                value.AOnly = reader.ReadString("AOnly");
            }

            public void SerializeDelta(
                in DerivedA value,
                in DerivedA baseValue,
                ISerializationWriter writer
            )
            {
                writer.BlitWriteDelta("Common", value.Common, baseValue.Common);
                writer.WriteStringDelta("AOnly", value.AOnly, baseValue.AOnly);
            }

            public void DeserializeDelta(
                ref DerivedA value,
                in DerivedA baseValue,
                ISerializationReader reader
            )
            {
                value ??= new DerivedA();
                var common = value.Common;
                reader.BlitReadDelta("Common", ref common, baseValue.Common);
                value.Common = common;
                value.AOnly = reader.ReadStringDelta("AOnly", baseValue.AOnly);
            }
        }

        public class DerivedBSerializer : ISerializer<DerivedB>, ISerializerDelta<DerivedB>
        {
            public DerivedBSerializer() { }

            public void Serialize(in DerivedB value, ISerializationWriter writer)
            {
                writer.Write("Common", value.Common);
                writer.Write("BOnly", value.BOnly);
            }

            public void Deserialize(ref DerivedB value, ISerializationReader reader)
            {
                value ??= new DerivedB();
                value.Common = reader.Read<int>("Common");
                value.BOnly = reader.Read<int>("BOnly");
            }

            public void SerializeDelta(
                in DerivedB value,
                in DerivedB baseValue,
                ISerializationWriter writer
            )
            {
                writer.BlitWriteDelta("Common", value.Common, baseValue.Common);
                writer.BlitWriteDelta("BOnly", value.BOnly, baseValue.BOnly);
            }

            public void DeserializeDelta(
                ref DerivedB value,
                in DerivedB baseValue,
                ISerializationReader reader
            )
            {
                value ??= new DerivedB();
                var common = value.Common;
                var bOnly = value.BOnly;
                reader.BlitReadDelta("Common", ref common, baseValue.Common);
                reader.BlitReadDelta("BOnly", ref bOnly, baseValue.BOnly);
                value.Common = common;
                value.BOnly = bOnly;
            }
        }
    }
}
