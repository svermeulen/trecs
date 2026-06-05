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
        SerializationHelper _helper;
        SerializationData _data;

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
            _helper = new SerializationHelper(_registry);
            _data = new SerializationData();
        }

        [Test]
        public void TypeId_BlitRoundTrip()
        {
            var original = new TypeId(0x12345678);
            _helper.WriteAll(_data, original, version: 1, includeTypeChecks: true);
            var roundTripped = _helper.ReadAll<TypeId>(_data);
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void Tag_BlitRoundTrip()
        {
            var original = new Tag(0x71000001);
            _helper.WriteAll(_data, original, version: 1, includeTypeChecks: true);
            var roundTripped = _helper.ReadAll<Tag>(_data);
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void SetId_BlitRoundTrip()
        {
            var original = new SetId(0x71000002);
            _helper.WriteAll(_data, original, version: 1, includeTypeChecks: true);
            var roundTripped = _helper.ReadAll<SetId>(_data);
            NAssert.AreEqual(original.Value, roundTripped.Value);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void ComponentTypeId_BlitRoundTrip()
        {
            var original = new ComponentTypeId(0x71000003);
            _helper.WriteAll(_data, original, version: 1, includeTypeChecks: true);
            var roundTripped = _helper.ReadAll<ComponentTypeId>(_data);
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
            _helper.WriteAll(_data, original, version: 1, includeTypeChecks: true);
            var roundTripped = _helper.ReadAll<TagSet>(_data);
            NAssert.AreEqual(original.Id, roundTripped.Id);
            NAssert.AreEqual(original, roundTripped);
        }

        [Test]
        public void ComponentTypeIdSet_BlitRoundTrip()
        {
            var original = new ComponentTypeIdSet(0x71000005);
            _helper.WriteAll(_data, original, version: 1, includeTypeChecks: true);
            var roundTripped = _helper.ReadAll<ComponentTypeIdSet>(_data);
            NAssert.AreEqual(original.Id, roundTripped.Id);
            NAssert.AreEqual(original, roundTripped);
        }
    }
}
