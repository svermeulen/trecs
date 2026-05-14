using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public partial struct SerTestInt : IEntityComponent
    {
        public int Value;
    }

    public partial struct SerTestFloat : IEntityComponent
    {
        public float Value;
    }

    public struct SerTag : ITag { }

    public struct SerPartitionA : ITag { }

    public struct SerPartitionB : ITag { }

    public partial class SerTestEntity
        : ITemplate,
            ITagged<SerTag>,
            IPartitionedBy<SerPartitionA>,
            IPartitionedBy<SerPartitionB>
    {
        SerTestInt TestInt;
        SerTestFloat TestFloat;
    }

    [TestFixture]
    public class WorldStateSerializerTests
    {
        static readonly TagSet PartitionA = TagSet.FromTags(
            Tag<SerTag>.Value,
            Tag<SerPartitionA>.Value
        );
        static readonly TagSet PartitionB = TagSet.FromTags(
            Tag<SerTag>.Value,
            Tag<SerPartitionB>.Value
        );

        SerializerRegistry _serializerRegistry;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
        }

        World CreateWorld()
        {
            var testGlobals = new Template(
                debugName: "TestGlobals",
                localBaseTemplates: new Template[] { TrecsTemplates.Globals.Template },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: Array.Empty<Tag>()
            );

            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );

            return new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(testGlobals)
                .AddTemplate(SerTestEntity.Template)
                .AddBlobStore(blobStore)
                .BuildAndInitialize();
        }

        byte[] SerializeWorld(World world)
        {
            var serializer = new WorldStateSerializer(world);
            using var writer = new BinarySerializationWriter(_serializerRegistry);
            writer.Start(version: 1, includeTypeChecks: false);
            serializer.SerializeState(writer);

            using var outputStream = new MemoryStream();
            using var outputWriter = new BinaryWriter(outputStream);
            writer.Complete(outputWriter);
            return outputStream.ToArray();
        }

        void DeserializeWorld(World world, byte[] data)
        {
            var serializer = new WorldStateSerializer(world);
            using var inputStream = new MemoryStream(data);
            using var inputReader = new BinaryReader(inputStream);
            var reader = new BinarySerializationReader(_serializerRegistry);
            reader.Start(inputReader);
            serializer.DeserializeState(reader);
        }

        #region Basic Round-Trip

        [Test]
        public void RoundTrip_SingleEntity_PreservesComponentData()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 42 })
                .Set(new SerTestFloat { Value = 3.14f })
                .AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));

            // Serialize
            var data = SerializeWorld(world);

            // Modify world state
            a.RemoveEntitiesWithTags(PartitionA);
            a.SubmitEntities();
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));

            // Deserialize — should restore the entity
            DeserializeWorld(world, data);

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            var comp = a.Query().WithTags(PartitionA).Single().Get<SerTestInt>();
            NAssert.AreEqual(42, comp.Read.Value);
        }

        #endregion

        #region Multiple Entities

        [Test]
        public void RoundTrip_MultipleEntities_AllPreserved()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(PartitionA)
                    .Set(new SerTestInt { Value = i * 10 })
                    .Set(new SerTestFloat { Value = i * 1.5f })
                    .AssertComplete();
            }
            a.SubmitEntities();

            var data = SerializeWorld(world);

            // Clear and restore
            a.RemoveEntitiesWithTags(PartitionA);
            a.SubmitEntities();

            DeserializeWorld(world, data);

            NAssert.AreEqual(5, a.CountEntitiesWithTags(PartitionA));
        }

        #endregion

        #region Multiple Groups (Partitions)

        [Test]
        public void RoundTrip_EntitiesInDifferentPartitions_PreservedCorrectly()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 100 })
                .Set(new SerTestFloat { Value = 1.0f })
                .AssertComplete();
            a.AddEntity(PartitionB)
                .Set(new SerTestInt { Value = 200 })
                .Set(new SerTestFloat { Value = 2.0f })
                .AssertComplete();
            a.SubmitEntities();

            var data = SerializeWorld(world);

            a.RemoveEntitiesWithTags(PartitionA);
            a.RemoveEntitiesWithTags(PartitionB);
            a.SubmitEntities();

            DeserializeWorld(world, data);

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));

            var compA = a.Query().WithTags(PartitionA).Single().Get<SerTestInt>();
            var compB = a.Query().WithTags(PartitionB).Single().Get<SerTestInt>();
            NAssert.AreEqual(100, compA.Read.Value);
            NAssert.AreEqual(200, compB.Read.Value);
        }

        #endregion

        #region Empty World

        [Test]
        public void RoundTrip_EmptyWorld_Succeeds()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            var data = SerializeWorld(world);
            DeserializeWorld(world, data);

            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionB));
        }

        #endregion

        #region Idempotent Deserialization

        [Test]
        public void RoundTrip_DeserializeTwice_ProducesSameResult()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 77 })
                .Set(new SerTestFloat { Value = 7.7f })
                .AssertComplete();
            a.SubmitEntities();

            var data = SerializeWorld(world);

            DeserializeWorld(world, data);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));

            DeserializeWorld(world, data);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));

            var comp = a.Query().WithTags(PartitionA).Single().Get<SerTestInt>();
            NAssert.AreEqual(77, comp.Read.Value);
        }

        #endregion

        #region Custom Component Serializers

        /// <summary>
        /// Stub custom serializer for <see cref="SerTestInt"/>. Writes per-element
        /// values via the typed <c>IComponentArray&lt;SerTestInt&gt;</c> interface
        /// so the round-trip test can run in safe-code-only test assemblies.
        /// </summary>
        class StubSerTestIntSerializer : IComponentArrayCustomSerializer
        {
            public int SerializeCallCount;
            public int DeserializeCallCount;

            public void Serialize(IComponentArray array, ISerializationWriter writer)
            {
                SerializeCallCount++;
                writer.Write("count", array.Count);
                var typed = (IComponentArray<SerTestInt>)array;
                for (int i = 0; i < array.Count; i++)
                {
                    var val = typed.GetValueAtIndexByRef(i).Value;
                    writer.Write("v", val);
                }
            }

            public void Deserialize(IComponentArray array, ISerializationReader reader)
            {
                DeserializeCallCount++;
                var count = reader.Read<int>("count");
                array.Clear();
                if (count > 0)
                {
                    array.EnsureCapacity(count);
                }
                array.SetCount(count);
                var typed = (IComponentArray<SerTestInt>)array;
                for (int i = 0; i < count; i++)
                {
                    typed.GetValueAtIndexByRef(i).Value = reader.Read<int>("v");
                }
            }
        }

        /// <summary>
        /// Inert stub used by registration-table tests that don't actually
        /// serialize. Throws if invoked.
        /// </summary>
        class InertCustomSerializer : IComponentArrayCustomSerializer
        {
            public void Serialize(IComponentArray array, ISerializationWriter writer)
            {
                throw new InvalidOperationException(
                    "Should not be called by registration-only tests"
                );
            }

            public void Deserialize(IComponentArray array, ISerializationReader reader)
            {
                throw new InvalidOperationException(
                    "Should not be called by registration-only tests"
                );
            }
        }

        [Test]
        public void Register_TryGet_ReturnsRegisteredSerializer()
        {
            using var world = CreateWorld();
            var serializer = new WorldStateSerializer(world);
            var custom = new InertCustomSerializer();

            serializer.RegisterCustomComponentSerializer<SerTestInt>(custom);

            NAssert.IsTrue(serializer.TryGetCustomComponentSerializer<SerTestInt>(out var actual));
            NAssert.AreSame(custom, actual);
        }

        [Test]
        public void TryGet_WhenNothingRegistered_ReturnsFalseAndNull()
        {
            using var world = CreateWorld();
            var serializer = new WorldStateSerializer(world);

            NAssert.IsFalse(serializer.TryGetCustomComponentSerializer<SerTestInt>(out var actual));
            NAssert.IsNull(actual);
        }

        [Test]
        public void Unregister_RemovesRegistration_AndReturnsTrueOnlyTheFirstTime()
        {
            using var world = CreateWorld();
            var serializer = new WorldStateSerializer(world);
            serializer.RegisterCustomComponentSerializer<SerTestInt>(new InertCustomSerializer());

            NAssert.IsTrue(serializer.UnregisterCustomComponentSerializer<SerTestInt>());
            NAssert.IsFalse(serializer.TryGetCustomComponentSerializer<SerTestInt>(out _));

            // Second unregister with nothing registered returns false.
            NAssert.IsFalse(serializer.UnregisterCustomComponentSerializer<SerTestInt>());
        }

        [Test]
        public void GetCustomComponentSerializerTypes_ReturnsRegisteredComponentTypes()
        {
            using var world = CreateWorld();
            var serializer = new WorldStateSerializer(world);

            NAssert.IsEmpty(serializer.GetCustomComponentSerializerTypes());

            serializer.RegisterCustomComponentSerializer<SerTestInt>(new InertCustomSerializer());
            serializer.RegisterCustomComponentSerializer<SerTestFloat>(new InertCustomSerializer());

            var types = serializer.GetCustomComponentSerializerTypes().ToList();
            CollectionAssert.AreEquivalent(
                new[] { typeof(SerTestInt), typeof(SerTestFloat) },
                types
            );

            serializer.UnregisterCustomComponentSerializer<SerTestInt>();

            types = serializer.GetCustomComponentSerializerTypes().ToList();
            CollectionAssert.AreEquivalent(new[] { typeof(SerTestFloat) }, types);
        }

        [Test]
        public void Register_Duplicate_OverwritesPreviousRegistration()
        {
            using var world = CreateWorld();
            var serializer = new WorldStateSerializer(world);
            var first = new InertCustomSerializer();
            var second = new InertCustomSerializer();

            serializer.RegisterCustomComponentSerializer<SerTestInt>(first);
            // The second registration should overwrite, not throw — this is a
            // behavior change from the previous Dictionary.Add semantics. The
            // implementation also logs a warning in this case.
            serializer.RegisterCustomComponentSerializer<SerTestInt>(second);

            NAssert.IsTrue(serializer.TryGetCustomComponentSerializer<SerTestInt>(out var actual));
            NAssert.AreSame(second, actual);
        }

        [Test]
        public void RoundTrip_InvokesRegisteredCustomSerializer()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 99 })
                .Set(new SerTestFloat { Value = 1.5f })
                .AssertComplete();
            a.SubmitEntities();

            var custom = new StubSerTestIntSerializer();

            byte[] data;
            using (var writer = new BinarySerializationWriter(_serializerRegistry))
            {
                writer.Start(version: 1, includeTypeChecks: false);
                var ws = new WorldStateSerializer(world);
                ws.RegisterCustomComponentSerializer<SerTestInt>(custom);
                ws.SerializeState(writer);
                using var outputStream = new MemoryStream();
                using var outputWriter = new BinaryWriter(outputStream);
                writer.Complete(outputWriter);
                data = outputStream.ToArray();
            }

            // The custom serializer is invoked once per group that has the
            // component type — the SerTestEntity template has two partitions
            // (A and B) so the SerTestInt array is materialized for both.
            NAssert.Greater(custom.SerializeCallCount, 0);

            // Wipe and restore through a serializer that has the same custom registration.
            a.RemoveEntitiesWithTags(PartitionA);
            a.SubmitEntities();

            using (var inputStream = new MemoryStream(data))
            using (var inputReader = new BinaryReader(inputStream))
            {
                var reader = new BinarySerializationReader(_serializerRegistry);
                reader.Start(inputReader);
                var ws = new WorldStateSerializer(world);
                ws.RegisterCustomComponentSerializer<SerTestInt>(custom);
                ws.DeserializeState(reader);
            }

            NAssert.AreEqual(custom.SerializeCallCount, custom.DeserializeCallCount);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(
                99,
                a.Query().WithTags(PartitionA).Single().Get<SerTestInt>().Read.Value
            );
        }

        #endregion
    }
}
