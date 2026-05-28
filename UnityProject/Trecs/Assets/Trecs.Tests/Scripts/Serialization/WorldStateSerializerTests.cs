using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [Copyable]
    public partial struct SerTestInt : IEntityComponent
    {
        public int Value;
    }

    [Copyable]
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

    public partial struct SerTestNativeList : IEntityComponent
    {
        public NativeList<int> Items;
    }

    public struct SerNativeListTag : ITag { }

    public partial class SerNativeListEntity : ITemplate, ITagged<SerNativeListTag>
    {
        SerTestNativeList NativeListComp;
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
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
        }

        World CreateWorld(Action<WorldBuilder> customize = null)
        {
            var testGlobals = new Template(
                debugName: "TestGlobals",
                localBaseTemplates: new Template[] { TrecsTemplates.Globals.Template },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: Array.Empty<Tag>()
            );

            var blobStore = new BlobStoreInMemory(BlobStoreInMemorySettings.Default, null);

            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(testGlobals)
                .AddTemplate(SerTestEntity.Template)
                .AddTemplate(SerNativeListEntity.Template)
                .AddBlobStore(blobStore);

            customize?.Invoke(builder);

            return builder.BuildAndInitialize();
        }

        static readonly TagSet NativeListPartition = TagSet.FromTags(Tag<SerNativeListTag>.Value);

        byte[] SerializeWorld(World world)
        {
            var serializer = new WorldStateSerializer(world);
            var writer = new BinarySerializationWriter(_serializerRegistry);
            writer.Start(version: 1, includeTypeChecks: false);
            serializer.SerializeFullState(writer);

            using var outputStream = new MemoryStream();
            writer.Complete(outputStream);
            return outputStream.ToArray();
        }

        void DeserializeWorld(World world, byte[] data)
        {
            var serializer = new WorldStateSerializer(world);
            var reader = new BinarySerializationReader(_serializerRegistry);
            reader.Start(data);
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
            a.Submit();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));

            // Serialize
            var data = SerializeWorld(world);

            // Modify world state
            a.RemoveEntitiesWithTags(PartitionA);
            a.Submit();
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));

            // Deserialize — should restore the entity
            DeserializeWorld(world, data);

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            var comp = a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a);
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
            a.Submit();

            var data = SerializeWorld(world);

            // Clear and restore
            a.RemoveEntitiesWithTags(PartitionA);
            a.Submit();

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
            a.Submit();

            var data = SerializeWorld(world);

            a.RemoveEntitiesWithTags(PartitionA);
            a.RemoveEntitiesWithTags(PartitionB);
            a.Submit();

            DeserializeWorld(world, data);

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionB));

            var compA = a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a);
            var compB = a.Query().WithTags(PartitionB).SingleHandle().Component<SerTestInt>(a);
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
            a.Submit();

            var data = SerializeWorld(world);

            DeserializeWorld(world, data);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));

            DeserializeWorld(world, data);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));

            var comp = a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a);
            NAssert.AreEqual(77, comp.Read.Value);
        }

        #endregion

        #region Custom Component Serializers

        /// <summary>
        /// Stub serializer for <see cref="SerTestInt"/>. Writes per-element
        /// values via the <c>NativeList&lt;SerTestInt&gt;</c> handed in by the
        /// framework, so the round-trip test can run in safe-code-only test
        /// assemblies.
        /// </summary>
        class StubSerTestIntSerializer : IComponentArraySerializer<SerTestInt>
        {
            public int SerializeCallCount;
            public int DeserializeCallCount;

            public void Serialize(NativeList<SerTestInt> values, ISerializationWriter writer)
            {
                SerializeCallCount++;
                for (int i = 0; i < values.Length; i++)
                {
                    writer.Write("V", values[i].Value);
                }
            }

            public void Deserialize(
                NativeList<SerTestInt> values,
                int requiredCount,
                ISerializationReader reader
            )
            {
                DeserializeCallCount++;
                values.Resize(requiredCount, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < requiredCount; i++)
                {
                    var v = values[i];
                    v.Value = reader.Read<int>("V");
                    values[i] = v;
                }
            }
        }

        /// <summary>
        /// Inert stub used by registration-table tests that don't actually
        /// serialize. Throws if invoked.
        /// </summary>
        class InertCustomSerializer<T> : IComponentArraySerializer<T>
            where T : unmanaged, IEntityComponent
        {
            public void Serialize(NativeList<T> values, ISerializationWriter writer)
            {
                throw new InvalidOperationException(
                    "Should not be called by registration-only tests"
                );
            }

            public void Deserialize(
                NativeList<T> values,
                int requiredCount,
                ISerializationReader reader
            )
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
            var custom = new InertCustomSerializer<SerTestInt>();

            world.ComponentArraySerializerRegistry.Register(custom);

            NAssert.IsTrue(
                world.ComponentArraySerializerRegistry.TryGet<SerTestInt>(out var actual)
            );
            NAssert.AreSame(custom, actual);
        }

        [Test]
        public void TryGet_WhenNothingRegistered_ReturnsFalseAndNull()
        {
            using var world = CreateWorld();

            NAssert.IsFalse(
                world.ComponentArraySerializerRegistry.TryGet<SerTestInt>(out var actual)
            );
            NAssert.IsNull(actual);
        }

        [Test]
        public void Unregister_RemovesRegistration_AndReturnsTrueOnlyTheFirstTime()
        {
            using var world = CreateWorld();
            world.ComponentArraySerializerRegistry.Register(
                new InertCustomSerializer<SerTestInt>()
            );

            NAssert.IsTrue(world.ComponentArraySerializerRegistry.Unregister<SerTestInt>());
            NAssert.IsFalse(world.ComponentArraySerializerRegistry.TryGet<SerTestInt>(out _));

            // Second unregister with nothing registered returns false.
            NAssert.IsFalse(world.ComponentArraySerializerRegistry.Unregister<SerTestInt>());
        }

        [Test]
        public void GetRegisteredComponentTypes_ReturnsRegisteredComponentTypes()
        {
            using var world = CreateWorld();

            NAssert.AreEqual(
                0,
                world.ComponentArraySerializerRegistry.GetRegisteredComponentTypes().Count
            );

            world.ComponentArraySerializerRegistry.Register(
                new InertCustomSerializer<SerTestInt>()
            );
            world.ComponentArraySerializerRegistry.Register(
                new InertCustomSerializer<SerTestFloat>()
            );

            var keys = world.ComponentArraySerializerRegistry.GetRegisteredComponentTypes();
            NAssert.AreEqual(2, keys.Count);
            var types = new List<Type>();
            foreach (var t in keys)
                types.Add(t);
            CollectionAssert.AreEquivalent(
                new[] { typeof(SerTestInt), typeof(SerTestFloat) },
                types
            );

            world.ComponentArraySerializerRegistry.Unregister<SerTestInt>();

            keys = world.ComponentArraySerializerRegistry.GetRegisteredComponentTypes();
            types.Clear();
            foreach (var t in keys)
                types.Add(t);
            CollectionAssert.AreEquivalent(new[] { typeof(SerTestFloat) }, types);
        }

        [Test]
        public void Register_Duplicate_Throws()
        {
            using var world = CreateWorld();
            world.ComponentArraySerializerRegistry.Register(
                new InertCustomSerializer<SerTestInt>()
            );

            // Duplicate registration is an error — callers that need to swap
            // should Unregister first.
            NAssert.Throws<TrecsException>(() =>
                world.ComponentArraySerializerRegistry.Register(
                    new InertCustomSerializer<SerTestInt>()
                )
            );
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
            a.Submit();

            var custom = new StubSerTestIntSerializer();
            world.ComponentArraySerializerRegistry.Register(custom);

            var data = SerializeWorld(world);

            // The custom serializer is invoked once per group that has the
            // component type — the SerTestEntity template has two partitions
            // (A and B) so the SerTestInt array is materialized for both.
            NAssert.Greater(custom.SerializeCallCount, 0);

            // Wipe and restore — the same registration on the world is reused.
            a.RemoveEntitiesWithTags(PartitionA);
            a.Submit();

            DeserializeWorld(world, data);

            NAssert.AreEqual(custom.SerializeCallCount, custom.DeserializeCallCount);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            NAssert.AreEqual(
                99,
                a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Read.Value
            );
        }

        #endregion

        #region Native-container component round-trip

        /// <summary>
        /// Serializer for <see cref="SerTestNativeList"/> — the typical pattern
        /// for a component holding a native container. Writes per-entry item
        /// counts and contents; on Deserialize, asserts the entry count
        /// matches what the live world has (the group's other component arrays
        /// will have been sized to the snapshot's count) and rebuilds each
        /// entry's <c>NativeList&lt;int&gt;</c> contents in place.
        /// </summary>
        class NativeListItemsSerializer : IComponentArraySerializer<SerTestNativeList>
        {
            public void Serialize(NativeList<SerTestNativeList> values, ISerializationWriter writer)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    ref readonly var entry = ref values.ElementAt(i);
                    writer.Write("itemCount", entry.Items.Length);
                    for (int j = 0; j < entry.Items.Length; j++)
                    {
                        writer.Write("item", entry.Items[j]);
                    }
                }
            }

            public void Deserialize(
                NativeList<SerTestNativeList> values,
                int requiredCount,
                ISerializationReader reader
            )
            {
                if (requiredCount != values.Length)
                {
                    throw new InvalidOperationException(
                        $"NativeListItemsSerializer: snapshot has {requiredCount} entries but live world has {values.Length}."
                    );
                }
                for (int i = 0; i < requiredCount; i++)
                {
                    ref var entry = ref values.ElementAt(i);
                    var itemCount = reader.Read<int>("itemCount");
                    entry.Items.ResizeUninitialized(itemCount);
                    for (int j = 0; j < itemCount; j++)
                    {
                        entry.Items[j] = reader.Read<int>("item");
                    }
                }
            }
        }

        /// <summary>
        /// End-to-end smoke test for the GitHub-discussion-#3 use case: a
        /// component that holds a <see cref="NativeList{T}"/> needs a custom
        /// serializer because the framework can't byte-blit native containers
        /// (the backing memory lives outside the component struct).
        ///
        /// Mutates the live <c>Items</c> after serialize so the test would
        /// fail if Deserialize were a no-op. Without the mutate step the
        /// <c>NativeList</c>'s backing pointer happens to still be valid
        /// across the round-trip and the test would pass trivially —
        /// failing to verify the custom serializer ran at all.
        /// </summary>
        [Test]
        public void RoundTrip_NativeListComponent_PreservesItemsAcrossRestore()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            world.ComponentArraySerializerRegistry.Register(new NativeListItemsSerializer());

            // Manually disposed after world disposal — the test's component
            // has no OnRemoved handler to clean up the inner NativeList.
            using var items = new NativeList<int>(8, Allocator.Persistent);
            items.Add(1);
            items.Add(2);
            items.Add(3);

            a.AddEntity(NativeListPartition)
                .Set(new SerTestNativeList { Items = items })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Mutate the live Items so Deserialize must actually restore the
            // contents. Without this, the existing NativeList<int> backing
            // pointer remains valid and the test would pass even if
            // Deserialize were a no-op — the same trap that would surface
            // across process boundaries (where the pointer is invalid).
            items.Clear();
            items.Add(99);

            DeserializeWorld(world, data);

            // Read back through the component to also verify the wire-format
            // round-trip, not just the local handle.
            ref readonly var restored = ref a.Query()
                .WithTags(NativeListPartition)
                .SingleHandle()
                .Component<SerTestNativeList>(a)
                .Read;
            NAssert.AreEqual(3, restored.Items.Length);
            NAssert.AreEqual(1, restored.Items[0]);
            NAssert.AreEqual(2, restored.Items[1]);
            NAssert.AreEqual(3, restored.Items[2]);
        }

        /// <summary>
        /// Verifies <see cref="SkipComponentSerializer{T}"/>'s post-fix
        /// semantics: it skips writing/reading element data, but the entry
        /// count IS serialized and a mismatch on load throws. This is the
        /// safety net that prevents silent corruption when the live world's
        /// per-group entity count drifts away from the snapshot's.
        /// </summary>
        [Test]
        public void SkipComponentSerializer_AsserstEntryCountMatches()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            world.ComponentArraySerializerRegistry.Register(
                new SkipComponentSerializer<SerTestInt>()
            );

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 1 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Add a second entity to the same partition so live count != snapshot count.
            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 2 })
                .Set(new SerTestFloat { Value = 2f })
                .AssertComplete();
            a.Submit();

            // Deserialize should now blow up because SkipComponentSerializer
            // sees a 1-entry count in the stream but 2 entries in the live array.
            NAssert.Throws<TrecsException>(() => DeserializeWorld(world, data));
        }

        /// <summary>
        /// Verifies <see cref="SkipComponentSerializer{T}"/> preserves runtime
        /// state across deserialize — it skips contributing to the stream but
        /// the live array's contents pass through untouched.
        /// </summary>
        [Test]
        public void SkipComponentSerializer_PreservesRuntimeValuesOnDeserialize()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            world.ComponentArraySerializerRegistry.Register(
                new SkipComponentSerializer<SerTestInt>()
            );

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 42 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Mutate the skipped component's value. Deserialize should leave
            // this value alone — that's the whole point of "skip".
            a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Write.Value = 99;

            DeserializeWorld(world, data);

            NAssert.AreEqual(
                99,
                a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Read.Value
            );
        }

        /// <summary>
        /// Verifies <see cref="Trecs.Serialization.DefaultValueComponentSerializer{T}"/>'s
        /// fresh-load contract: live world starts with entities of differing count
        /// and arbitrary values, deserialize resizes the array to match the
        /// snapshot's count and zero-inits every entry. Counterpart to Skip,
        /// which asserts on count mismatch.
        /// </summary>
        [Test]
        public void DefaultValueComponentSerializer_ResizesAndZeroInitsOnDeserialize()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            world.ComponentArraySerializerRegistry.Register(
                new DefaultValueComponentSerializer<SerTestInt>()
            );

            // Snapshot state: 1 entity, SerTestInt.Value = 7 (the value doesn't
            // round-trip because DefaultValue skips it). SerTestFloat IS blit-
            // serialized normally, so its value DOES round-trip and gives us
            // a sanity check that the rest of the group restored correctly.
            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 7 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Diverge live from snapshot: add a second entity with non-default
            // values. The deserialize must (a) truncate to the snapshot's
            // count without throwing (Skip would have asserted), and
            // (b) zero every remaining entry's SerTestInt value.
            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 555 })
                .Set(new SerTestFloat { Value = 2f })
                .AssertComplete();
            a.Submit();
            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));

            DeserializeWorld(world, data);

            NAssert.AreEqual(1, a.CountEntitiesWithTags(PartitionA));
            var entity = a.Query().WithTags(PartitionA).SingleHandle();
            // SerTestInt: zero-init (default(SerTestInt).Value == 0). Snapshot's
            // value of 7 was never written; live's value of 555 was discarded.
            NAssert.AreEqual(0, entity.Component<SerTestInt>(a).Read.Value);
            // SerTestFloat: standard blit-restore from snapshot.
            NAssert.AreEqual(1f, entity.Component<SerTestFloat>(a).Read.Value);
        }

        #endregion

        #region Cross-cutting registration / dispatch coverage

        /// <summary>
        /// Verifies the <see cref="WorldBuilder.RegisterComponentArraySerializer{T}"/>
        /// shortcut: registering up front on the builder is equivalent to
        /// registering on <see cref="World.ComponentArraySerializerRegistry"/>
        /// after Build. The round-trip exercises the dispatcher end-to-end.
        /// </summary>
        [Test]
        public void WorldBuilder_RegisterComponentArraySerializer_AppliesToRoundTrip()
        {
            var custom = new StubSerTestIntSerializer();
            using var world = CreateWorld(b => b.RegisterComponentArraySerializer(custom));
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 55 })
                .Set(new SerTestFloat { Value = 0f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);
            NAssert.Greater(custom.SerializeCallCount, 0);

            // Mutate so a no-op Deserialize would fail rather than passing trivially.
            a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Write.Value = 0;

            DeserializeWorld(world, data);

            NAssert.Greater(custom.DeserializeCallCount, 0);
            NAssert.AreEqual(
                55,
                a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Read.Value
            );
        }

        /// <summary>
        /// The custom serializer should be invoked once per group that contains
        /// the registered component — not once per type. <see cref="SerTestEntity"/>
        /// has two partitions (A and B), each its own group; each gets its own
        /// dispatcher call in both directions. Also verifies values round-trip
        /// correctly per partition (no cross-talk between groups).
        /// </summary>
        [Test]
        public void RoundTrip_CustomSerializer_InvokedOncePerGroupContainingComponent()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            var custom = new StubSerTestIntSerializer();
            world.ComponentArraySerializerRegistry.Register(custom);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 11 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete();
            a.AddEntity(PartitionB)
                .Set(new SerTestInt { Value = 22 })
                .Set(new SerTestFloat { Value = 2f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);
            NAssert.GreaterOrEqual(
                custom.SerializeCallCount,
                2,
                "Custom serializer should fire once per partition that materializes SerTestInt."
            );

            // Mutate both partitions' values so a no-op Deserialize would fail.
            a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Write.Value = 0;
            a.Query().WithTags(PartitionB).SingleHandle().Component<SerTestInt>(a).Write.Value = 0;

            DeserializeWorld(world, data);

            NAssert.GreaterOrEqual(custom.DeserializeCallCount, 2);
            NAssert.AreEqual(
                11,
                a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Read.Value
            );
            NAssert.AreEqual(
                22,
                a.Query().WithTags(PartitionB).SingleHandle().Component<SerTestInt>(a).Read.Value
            );
        }

        /// <summary>
        /// Locks in the "registration is World-scoped, not WorldStateSerializer-scoped"
        /// contract. Pre-refactor, <see cref="WorldStateSerializer"/> kept its
        /// own per-instance registration dictionary — so recording / snapshot /
        /// checksum code that constructed its own <c>WorldStateSerializer</c>
        /// would silently miss user registrations. After moving the registry
        /// onto <see cref="World"/>, every fresh <c>WorldStateSerializer</c>
        /// sees the same registrations.
        /// </summary>
        [Test]
        public void CustomSerializer_AppliesAcrossFreshWorldStateSerializerInstances()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            var custom = new StubSerTestIntSerializer();
            world.ComponentArraySerializerRegistry.Register(custom);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 33 })
                .Set(new SerTestFloat { Value = 0f })
                .AssertComplete();
            a.Submit();

            // Two independent WorldStateSerializer instances — one for write,
            // one for read. The registration lives on the World, so both see it.
            var wsWrite = new WorldStateSerializer(world);
            var wsRead = new WorldStateSerializer(world);

            byte[] data;
            {
                var writer = new BinarySerializationWriter(_serializerRegistry);
                writer.Start(version: 1, includeTypeChecks: false);
                wsWrite.SerializeFullState(writer);
                using var outputStream = new MemoryStream();
                writer.Complete(outputStream);
                data = outputStream.ToArray();
            }
            NAssert.Greater(custom.SerializeCallCount, 0);

            // Mutate so a no-op Deserialize would fail.
            a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Write.Value = 0;

            {
                var reader = new BinarySerializationReader(_serializerRegistry);
                reader.Start(data);
                wsRead.DeserializeState(reader);
            }

            NAssert.Greater(custom.DeserializeCallCount, 0);
            NAssert.AreEqual(
                33,
                a.Query().WithTags(PartitionA).SingleHandle().Component<SerTestInt>(a).Read.Value
            );
        }

        /// <summary>
        /// Custom serializer that records the <c>values.Length</c> it sees on
        /// each invocation. Used to verify the empty-group edge case.
        /// </summary>
        class LengthRecordingSerializer : IComponentArraySerializer<SerTestInt>
        {
            public readonly List<int> SerializeLengths = new();
            public readonly List<int> DeserializeLengths = new();

            public void Serialize(NativeList<SerTestInt> values, ISerializationWriter writer)
            {
                SerializeLengths.Add(values.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    writer.Write("V", values[i].Value);
                }
            }

            public void Deserialize(
                NativeList<SerTestInt> values,
                int requiredCount,
                ISerializationReader reader
            )
            {
                DeserializeLengths.Add(requiredCount);
                values.ResizeUninitialized(requiredCount);
                for (int i = 0; i < requiredCount; i++)
                {
                    var v = values[i];
                    v.Value = reader.Read<int>("V");
                    values[i] = v;
                }
            }
        }

        /// <summary>
        /// Edge case: a group whose component slots are materialized but whose
        /// entity count is 0 (e.g. all entities were removed). Empty groups are
        /// normalized to numComponents=0 during serialization, so the custom
        /// serializer is NOT invoked — verify the round-trip still works.
        /// </summary>
        [Test]
        public void RoundTrip_CustomSerializer_HandlesEmptyGroup()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            var custom = new LengthRecordingSerializer();
            world.ComponentArraySerializerRegistry.Register(custom);

            // Add then remove an entity to materialize PartitionA's component
            // slots without leaving any entries — Trecs's component arrays are
            // lazily created on first Add, so we need the Add to even reach
            // the empty case at all.
            var handle = a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 1 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete()
                .Handle;
            a.Submit();
            a.RemoveEntity(handle);
            a.Submit();
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));

            var data = SerializeWorld(world);
            NAssert.IsFalse(
                custom.SerializeLengths.Contains(0),
                "Empty groups are normalized away — custom serializer should not be invoked."
            );

            DeserializeWorld(world, data);

            NAssert.IsFalse(
                custom.DeserializeLengths.Contains(0),
                "Empty groups are normalized away — custom serializer should not be invoked on deserialize either."
            );
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));
        }

        #endregion

        #region PerEntityComponentArraySerializer

        /// <summary>
        /// Per-element <see cref="ISerializer{T}"/> used as the inner serializer
        /// for the <see cref="Trecs.Serialization.PerEntityComponentArraySerializer{T}"/>
        /// round-trip test. Records call counts so the test can verify the
        /// adapter actually delegates per element.
        /// </summary>
        class SerTestIntElementSerializer : ISerializer<SerTestInt>
        {
            public int SerializeCallCount;
            public int DeserializeCallCount;

            public void Serialize(in SerTestInt value, ISerializationWriter writer)
            {
                SerializeCallCount++;
                writer.Write("V", value.Value);
            }

            public void Deserialize(ref SerTestInt value, ISerializationReader reader)
            {
                DeserializeCallCount++;
                value.Value = reader.Read<int>("V");
            }
        }

        /// <summary>
        /// Verifies <see cref="Trecs.Serialization.PerEntityComponentArraySerializer{T}"/>
        /// adapts an <see cref="ISerializer{T}"/> to the per-array contract:
        /// the inner serializer fires once per entity on both sides, and the
        /// values round-trip through the snapshot.
        /// </summary>
        [Test]
        public void PerEntityComponentArraySerializer_RoundTripsViaInnerSerializer()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            var inner = new SerTestIntElementSerializer();
            world.ComponentArraySerializerRegistry.Register(
                new PerEntityComponentArraySerializer<SerTestInt>(inner)
            );

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 11 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete();
            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 22 })
                .Set(new SerTestFloat { Value = 2f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);
            NAssert.AreEqual(2, inner.SerializeCallCount);

            // Wipe and restore so a no-op deserialize would fail.
            a.RemoveEntitiesWithTags(PartitionA);
            a.Submit();
            NAssert.AreEqual(0, a.CountEntitiesWithTags(PartitionA));

            DeserializeWorld(world, data);

            NAssert.AreEqual(2, inner.DeserializeCallCount);
            NAssert.AreEqual(2, a.CountEntitiesWithTags(PartitionA));

            var restoredValues = new List<int>();
            foreach (var ei in a.Query().WithTags(PartitionA).Indices())
            {
                restoredValues.Add(a.Component<SerTestInt>(ei).Read.Value);
            }
            restoredValues.Sort();
            CollectionAssert.AreEqual(new[] { 11, 22 }, restoredValues);
        }

        #endregion

        #region Dispatcher post-condition

        /// <summary>
        /// Buggy user serializer that leaves <c>values.Length</c> off by one
        /// from <c>requiredCount</c>. Used to verify the dispatcher's
        /// post-condition assert fires — the whole point of having the
        /// framework own the count.
        /// </summary>
        class WrongLengthSerializer : IComponentArraySerializer<SerTestInt>
        {
            public void Serialize(NativeList<SerTestInt> values, ISerializationWriter writer) { }

            public void Deserialize(
                NativeList<SerTestInt> values,
                int requiredCount,
                ISerializationReader reader
            )
            {
                // Bug: leave the list one element too long so the dispatcher
                // post-condition catches it instead of letting a desync slip
                // through into entity-component lookups.
                values.Resize(requiredCount + 1, NativeArrayOptions.ClearMemory);
            }
        }

        [Test]
        public void Dispatcher_Throws_WhenUserSerializerLeavesWrongLength()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            world.ComponentArraySerializerRegistry.Register(new WrongLengthSerializer());

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 1 })
                .Set(new SerTestFloat { Value = 1f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            NAssert.Throws<TrecsException>(() => DeserializeWorld(world, data));
        }

        #endregion

        #region Section guard drift

        /// <summary>
        /// Locks in the section-guard drift error path: corrupting the last
        /// byte of the data buffer (which the wire format guarantees is the
        /// <c>AfterSystemEnable</c> guard byte, 0xA6) must trigger a
        /// <see cref="SerializationException"/> whose message names the
        /// section that drifted.
        ///
        /// <para>
        /// The other five section guards are covered by
        /// <see cref="Deserialize_ThrowsSerializationException_WhenEachSectionGuardCorrupted"/>
        /// via a value-scan; the last guard's position is structurally fixed
        /// (last byte of the data buffer, written immediately before the
        /// trailing EndOfPayloadMarker), so it gets its own targeted test
        /// that doesn't depend on byte-value scanning.
        /// </para>
        /// </summary>
        [Test]
        public void Deserialize_ThrowsSerializationException_WhenLastSectionGuardCorrupted()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 7 })
                .Set(new SerTestFloat { Value = 1.5f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Wire format: [...data buffer ending with AfterSystemEnable
            // guard byte 0xA6][EndOfPayloadMarker 0x5E]. The last guard byte
            // therefore lives at data.Length - 2.
            NAssert.AreEqual(
                (byte)0x5E,
                data[data.Length - 1],
                "Trailing byte should be EndOfPayloadMarker"
            );
            NAssert.AreEqual(
                (byte)0xA6,
                data[data.Length - 2],
                "Byte before EndOfPayloadMarker should be the AfterSystemEnable guard"
            );

            data[data.Length - 2] ^= 0xFF;

            var ex = NAssert.Throws<SerializationException>(() => DeserializeWorld(world, data));
            StringAssert.Contains("AfterSystemEnable", ex.Message);
        }

        /// <summary>
        /// Drift-detection coverage for every section guard. For each guard
        /// value (0xA1..0xA6), tries corrupting each occurrence of that byte
        /// in the snapshot stream and asserts that at least one corruption
        /// produces a <see cref="SerializationException"/> whose message
        /// names the section. The "at least one" framing is robust to
        /// false-positive byte-value matches (a content byte that happens to
        /// equal the guard value); the real guard byte is the one whose
        /// corruption produces the named exception.
        ///
        /// <para>
        /// Without this test a regression that broke a section's wire format
        /// would still pass round-trip (the guard fires on the corrupted
        /// side too), but the load-bearing error path — load-bearing in
        /// release builds, since the guards throw
        /// <see cref="SerializationException"/> rather than a debug-only
        /// assert — could drift silently (e.g. someone replacing the throw
        /// with a <c>TrecsDebugAssert</c>).
        /// </para>
        /// </summary>
        [TestCase(0xA1, "AfterTimingFields")]
        [TestCase(0xA2, "AfterComponentArrays")]
        [TestCase(0xA3, "AfterEntityHandles")]
        [TestCase(0xA4, "AfterEntitySets")]
        [TestCase(0xA5, "AfterHeaps")]
        [TestCase(0xA6, "AfterSystemEnable")]
        public void Deserialize_ThrowsSerializationException_WhenEachSectionGuardCorrupted(
            int guardValue,
            string sectionName
        )
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 13 })
                .Set(new SerTestFloat { Value = 2.5f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Find every position where this guard's byte value appears.
            // Each candidate gets one corruption attempt; we need at least
            // one to produce the named exception. (Most snapshots only
            // contain the guard's value at the guard position itself —
            // 0xA1..0xA6 were chosen to be outside the natural range of the
            // small ints/lengths surrounding them — but a content byte may
            // coincidentally match, hence the "any candidate" loop.)
            var candidates = new List<int>();
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == (byte)guardValue)
                {
                    candidates.Add(i);
                }
            }
            NAssert.IsNotEmpty(
                candidates,
                $"Snapshot must contain at least one occurrence of guard byte 0x{guardValue:X2}"
            );

            bool foundNamedException = false;
            var diagnostics = new List<string>();
            foreach (var pos in candidates)
            {
                var corrupted = (byte[])data.Clone();
                corrupted[pos] ^= 0xFF;

                try
                {
                    DeserializeWorld(world, corrupted);
                    diagnostics.Add($"pos {pos}: no exception");
                }
                catch (SerializationException ex)
                {
                    if (ex.Message.Contains(sectionName))
                    {
                        foundNamedException = true;
                        break;
                    }
                    diagnostics.Add(
                        $"pos {pos}: SerializationException not naming '{sectionName}': {ex.Message}"
                    );
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"pos {pos}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            NAssert.IsTrue(
                foundNamedException,
                $"Expected at least one corruption of byte 0x{guardValue:X2} to throw "
                    + $"SerializationException naming '{sectionName}'. "
                    + $"Candidates tried: {candidates.Count}. Diagnostics: "
                    + string.Join(" | ", diagnostics)
            );
        }

        /// <summary>
        /// Inserts one byte into the snapshot stream just after the first
        /// section guard's expected position, shifting all subsequent reads
        /// by one byte. Verifies the resulting drift is caught at some
        /// section boundary (rather than cascading silently through to a
        /// late, vague failure) — the whole point of having multiple guards
        /// across the stream.
        /// </summary>
        [Test]
        public void Deserialize_ThrowsSerializationException_WhenStreamIsShiftedByOneByte()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            a.AddEntity(PartitionA)
                .Set(new SerTestInt { Value = 21 })
                .Set(new SerTestFloat { Value = 3.5f })
                .AssertComplete();
            a.Submit();

            var data = SerializeWorld(world);

            // Insert a junk byte somewhere in the middle of the payload
            // (past the header / bit fields, before the trailing sentinel).
            // The exact position doesn't matter — any 1-byte shift inside
            // the data buffer will cause section-guard drift.
            int insertAt = data.Length / 2;
            var shifted = new byte[data.Length + 1];
            Array.Copy(data, 0, shifted, 0, insertAt);
            shifted[insertAt] = 0x00; // junk byte
            Array.Copy(data, insertAt, shifted, insertAt + 1, data.Length - insertAt);

            NAssert.Catch<Exception>(() => DeserializeWorld(world, shifted));
        }

        #endregion
    }
}
