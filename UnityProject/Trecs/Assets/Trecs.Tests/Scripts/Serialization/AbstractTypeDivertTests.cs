using System;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Locks in the wire format for the abstract-T divert in
    // ISerializationWriter.Write<T>/WriteDelta<T> and ISerializationReader.
    // Read<T>/ReadDelta<T>: when T is abstract, the typed entry points forward
    // to the WriteObject/ReadObject family so the runtime concrete-type id is
    // emitted into the payload. Roundtripping via the typed entry points must
    // produce the correct concrete subtype.
    [TestFixture]
    public class AbstractTypeDivertTests
    {
        SerializerRegistry _serializerRegistry;
        SerializationHelper _helper;
        SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();

            _serializerRegistry.RegisterSerializer(new DerivedASerializer());
            _serializerRegistry.RegisterSerializer(new DerivedBSerializer());
            _serializerRegistry.RegisterSerializerDelta<DerivedASerializer>();
            _serializerRegistry.RegisterSerializerDelta<DerivedBSerializer>();

            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void Write_TAbstract_RoundtripsConcreteSubtype()
        {
            AbstractBase original = new DerivedA { Common = 42, AOnly = "alpha" };

            _helper.WriteAll<AbstractBase>(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true
            );
            var result = _helper.ReadAll<AbstractBase>(_data);

            NAssert.IsNotNull(result);
            NAssert.IsInstanceOf<DerivedA>(result);
            var derived = (DerivedA)result;
            NAssert.That(derived.Common == 42);
            NAssert.That(derived.AOnly == "alpha");
        }

        [Test]
        public void Write_TAbstract_DistinguishesSubtypesByRuntimeType()
        {
            AbstractBase a = new DerivedA { Common = 1, AOnly = "x" };

            _helper.WriteAll<AbstractBase>(
                _data,
                a,
                TestConstants.Version,
                includeTypeChecks: true
            );
            var resultA = _helper.ReadAll<AbstractBase>(_data);

            AbstractBase b = new DerivedB { Common = 2, BOnly = 7 };

            _helper.WriteAll<AbstractBase>(
                _data,
                b,
                TestConstants.Version,
                includeTypeChecks: true
            );
            var resultB = _helper.ReadAll<AbstractBase>(_data);

            NAssert.IsInstanceOf<DerivedA>(resultA);
            NAssert.IsInstanceOf<DerivedB>(resultB);
            NAssert.That(((DerivedA)resultA).AOnly == "x");
            NAssert.That(((DerivedB)resultB).BOnly == 7);
        }

        [Test]
        public void Write_TAbstract_RoundtripsWithoutIncludeTypeChecks()
        {
            // The divert always emits the type id (via WriteObject) regardless
            // of includeTypeChecks, so reading must still succeed when the
            // toggle is off.
            AbstractBase original = new DerivedB { Common = 9, BOnly = 99 };

            _helper.WriteAll<AbstractBase>(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: false
            );
            var result = _helper.ReadAll<AbstractBase>(_data);

            NAssert.IsInstanceOf<DerivedB>(result);
            var derived = (DerivedB)result;
            NAssert.That(derived.Common == 9);
            NAssert.That(derived.BOnly == 99);
        }

        [Test]
        public void WriteDelta_TAbstract_RoundtripsChangedConcreteSubtype()
        {
            AbstractBase baseValue = new DerivedA { Common = 1, AOnly = "old" };
            AbstractBase changed = new DerivedA { Common = 2, AOnly = "new" };

            _helper.WriteAllDelta<AbstractBase>(
                _data,
                changed,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true
            );
            var result = _helper.ReadAllDelta<AbstractBase>(_data, baseValue);

            NAssert.IsInstanceOf<DerivedA>(result);
            var derived = (DerivedA)result;
            NAssert.That(derived.Common == 2);
            NAssert.That(derived.AOnly == "new");
        }

        [Test]
        public void WriteDelta_TAbstract_RoundtripsUnchangedConcreteSubtype()
        {
            AbstractBase baseValue = new DerivedB { Common = 5, BOnly = 50 };
            AbstractBase same = new DerivedB { Common = 5, BOnly = 50 };

            _helper.WriteAllDelta<AbstractBase>(
                _data,
                same,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true
            );
            var result = _helper.ReadAllDelta<AbstractBase>(_data, baseValue);

            NAssert.IsInstanceOf<DerivedB>(result);
            var derived = (DerivedB)result;
            NAssert.That(derived.Common == 5);
            NAssert.That(derived.BOnly == 50);
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
