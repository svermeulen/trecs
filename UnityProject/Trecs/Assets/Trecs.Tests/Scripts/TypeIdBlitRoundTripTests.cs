using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Byte-layout verification: each type-id wrapper is a single-int readonly struct,
    // so RegisterBlit<T> just memcpy's it through. These tests catch a regression where
    // a wrapper gains a non-blittable field (or where the inner field is reordered) —
    // either would break every save file that goes through RegisterBlit.
    [TestFixture]
    public class TypeIdBlitRoundTripTests
    {
        SerializerRegistry _registry;
        SerializationBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            _registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_registry);
            // ComponentTypeId, ComponentTypeIdSet, and Tag aren't in the default set —
            // register them ad-hoc here for the round-trip.
            _registry.RegisterSerializer(new BlitSerializer<ComponentTypeId>());
            _registry.RegisterSerializer(new BlitSerializer<ComponentTypeIdSet>());
            _registry.RegisterSerializer(new BlitSerializer<Tag>());
            _buffer = new SerializationBuffer(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
        }

        [Test]
        public void TypeId_BlitRoundTrip()
        {
            var original = new TypeId(0x12345678);
            _buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var roundTripped = _buffer.ReadAll<TypeId>();
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void Tag_BlitRoundTrip()
        {
            var original = new Tag(0x71000001);
            _buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var roundTripped = _buffer.ReadAll<Tag>();
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void SetId_BlitRoundTrip()
        {
            var original = new SetId(0x71000002);
            _buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var roundTripped = _buffer.ReadAll<SetId>();
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void ComponentTypeId_BlitRoundTrip()
        {
            var original = new ComponentTypeId(0x71000003);
            _buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var roundTripped = _buffer.ReadAll<ComponentTypeId>();
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        // Set wrappers: round-trip preserves the int id. Member-list retrieval would
        // require the id to be interned in TypeIdSetRegistry, which isn't what this
        // test is verifying — that's covered by TagSetRegistryTests.
        [Test]
        public void TagSet_BlitRoundTrip()
        {
            var original = new TagSet(0x71000004);
            _buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var roundTripped = _buffer.ReadAll<TagSet>();
            NAssert.AreEqual(original.Id, roundTripped.Id);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void ComponentTypeIdSet_BlitRoundTrip()
        {
            var original = new ComponentTypeIdSet(0x71000005);
            _buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            var roundTripped = _buffer.ReadAll<ComponentTypeIdSet>();
            NAssert.AreEqual(original.Id, roundTripped.Id);
            NAssert.AreEqual(original, roundTripped);
        }
    }
}
