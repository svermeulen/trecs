using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class SerializerRegistryRegistrationTests
    {
        SerializerRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new SerializerRegistry();
        }

        [Test]
        public void RegisterSerializer_Generic_AddsExpectedSerializer()
        {
            _registry.RegisterSerializer<ListSerializer<int>>();

            Assert.IsTrue(_registry.HasSerializer<List<int>>());
            Assert.IsInstanceOf<ListSerializer<int>>(_registry.GetSerializer<List<int>>());
        }

        [Test]
        public void RegisterSerializer_GenericTwice_SecondCallIsNoOp()
        {
            _registry.RegisterSerializer<ListSerializer<int>>();
            var first = _registry.GetSerializer<List<int>>();

            // Same Type registered again — should be silently deduped.
            _registry.RegisterSerializer<ListSerializer<int>>();

            Assert.AreSame(first, _registry.GetSerializer<List<int>>());
        }

        [Test]
        public void RegisterSerializer_RuntimeTypeTwice_SecondCallIsNoOp()
        {
            _registry.RegisterSerializer(typeof(ListSerializer<int>));
            var first = _registry.GetSerializer<List<int>>();

            _registry.RegisterSerializer(typeof(ListSerializer<int>));

            Assert.AreSame(first, _registry.GetSerializer<List<int>>());
        }

        [Test]
        public void RegisterSerializer_GenericAfterRuntimeType_StillDedupes()
        {
            _registry.RegisterSerializer(typeof(ListSerializer<int>));
            var first = _registry.GetSerializer<List<int>>();

            // Same underlying Type via the generic path — should still dedup.
            _registry.RegisterSerializer<ListSerializer<int>>();

            Assert.AreSame(first, _registry.GetSerializer<List<int>>());
        }

        [Test]
        public void RegisterSerializer_InstanceThenGeneric_Throws()
        {
            _registry.RegisterSerializer(new ListSerializer<int>());

            // Instance registration is exclusive — even the same serializer Type
            // can't be re-registered via the generic overload afterward.
            TrecsDebugAssert.Throws<TrecsException>(() =>
                _registry.RegisterSerializer<ListSerializer<int>>()
            );
        }

        [Test]
        public void RegisterSerializer_GenericThenInstance_Throws()
        {
            _registry.RegisterSerializer<ListSerializer<int>>();

            TrecsDebugAssert.Throws<TrecsException>(() =>
                _registry.RegisterSerializer(new ListSerializer<int>())
            );
        }

        [Test]
        public void RegisterSerializer_TwoDifferentTypesSameTarget_Throws()
        {
            _registry.RegisterSerializer<ListSerializer<int>>();

            // Different serializer Type targeting the same object type — bug,
            // should throw rather than silently overwrite.
            TrecsDebugAssert.Throws<TrecsException>(() =>
                _registry.RegisterSerializer<AlternateListIntSerializer>()
            );
        }

        [Test]
        public void RegisterSerializerDelta_GenericTwice_SecondCallIsNoOp()
        {
            _registry.RegisterSerializerDelta<BlitSerializer<int>>();
            var first = _registry.GetSerializerDelta<int>();

            _registry.RegisterSerializerDelta<BlitSerializer<int>>();

            Assert.AreSame(first, _registry.GetSerializerDelta<int>());
        }

        [Test]
        public void RegisterSerializerDelta_RuntimeTypeTwice_SecondCallIsNoOp()
        {
            _registry.RegisterSerializerDelta(typeof(BlitSerializer<int>));
            var first = _registry.GetSerializerDelta<int>();

            _registry.RegisterSerializerDelta(typeof(BlitSerializer<int>));

            Assert.AreSame(first, _registry.GetSerializerDelta<int>());
        }

        [Test]
        public void RegisterSerializerDelta_InstanceThenGeneric_Throws()
        {
            _registry.RegisterSerializerDelta(new BlitSerializer<int>());

            TrecsDebugAssert.Throws<TrecsException>(() =>
                _registry.RegisterSerializerDelta<BlitSerializer<int>>()
            );
        }

        [Test]
        public void RegisterSerializer_Generic_DefersConstructionUntilFirstLookup()
        {
            CountingSerializer.ConstructionCount = 0;

            _registry.RegisterSerializer<CountingSerializer>();
            Assert.AreEqual(
                0,
                CountingSerializer.ConstructionCount,
                "Type-based registration must not eagerly construct the serializer"
            );

            // HasSerializer must reflect the pending registration without
            // forcing materialization.
            Assert.IsTrue(_registry.HasSerializer<CountingTarget>());
            Assert.AreEqual(0, CountingSerializer.ConstructionCount);

            var first = _registry.GetSerializer<CountingTarget>();
            Assert.AreEqual(1, CountingSerializer.ConstructionCount);

            var second = _registry.GetSerializer<CountingTarget>();
            Assert.AreEqual(
                1,
                CountingSerializer.ConstructionCount,
                "Materialized instance must be cached"
            );
            Assert.AreSame(first, second);
        }

        sealed class AlternateListIntSerializer : ISerializer<List<int>>
        {
            public void Serialize(in List<int> value, ISerializationWriter writer) =>
                throw new NotImplementedException();

            public void Deserialize(ref List<int> value, ISerializationReader reader) =>
                throw new NotImplementedException();
        }

        sealed class CountingTarget { }

        sealed class CountingSerializer : ISerializer<CountingTarget>
        {
            public static int ConstructionCount;

            public CountingSerializer()
            {
                ConstructionCount++;
            }

            public void Serialize(in CountingTarget value, ISerializationWriter writer) =>
                throw new NotImplementedException();

            public void Deserialize(ref CountingTarget value, ISerializationReader reader) =>
                throw new NotImplementedException();
        }
    }
}
