using System.IO;
using System.Reflection;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    partial class SchemaFingerprintTestSystemA : ISystem
    {
        public void Execute() { }
    }

    /// <summary>
    /// Tests for <see cref="WorldSchemaFingerprint"/>: stability across
    /// identically-built worlds, sensitivity of each sub-hash to its schema
    /// aspect, and the load-time gate that rejects schema-stale snapshots
    /// with an explanation instead of a misaligned binary read.
    /// </summary>
    [TestFixture]
    public class WorldSchemaFingerprintTests
    {
        class InertComponentSerializer<T> : IComponentArraySerializer<T>
            where T : unmanaged, IEntityComponent
        {
            public void Serialize(NativeList<T> values, ISerializationWriter writer) { }

            public void Deserialize(
                NativeList<T> values,
                int requiredCount,
                ISerializationReader reader
            )
            {
                values.Resize(requiredCount, NativeArrayOptions.ClearMemory);
            }
        }

        static SnapshotSerializer CreateSnapshotSerializer(World world)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            return new SnapshotSerializer(world, registry, new WorldStateSerializer(world));
        }

        [Test]
        public void Fingerprint_IdenticallyBuiltWorlds_AreEqual()
        {
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envB = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);

            NAssert.AreEqual(envA.World.SchemaFingerprint, envB.World.SchemaFingerprint);
        }

        [Test]
        public void Fingerprint_ExtraComponentOnTemplate_ChangesOnlyGroupsHash()
        {
            // Same tag, same template name — the only difference is one extra
            // component declaration. This is the canonical "code changed,
            // old snapshot is stale" scenario.
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envB = EcsTestHelper.CreateEnvironment(
                TestTemplate
                    .Named("TestSimpleAlpha")
                    .WithTags(TestTags.Alpha)
                    .WithComponent<TestInt>(default(TestInt))
                    .WithComponent<TestFloat>(default(TestFloat))
            );

            var a = envA.World.SchemaFingerprint;
            var b = envB.World.SchemaFingerprint;

            NAssert.AreNotEqual(a.GroupsHash, b.GroupsHash);
            NAssert.AreEqual(a.SetsHash, b.SetsHash);
            NAssert.AreEqual(a.CustomSerializersHash, b.CustomSerializersHash);
        }

        [Test]
        public void Fingerprint_VariableUpdateOnlyToggle_ChangesGroupsHash()
        {
            // VUO components serialize as a count instead of a full array, so
            // flipping the flag changes the wire shape even though the
            // component set is identical.
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envB = EcsTestHelper.CreateEnvironment(
                TestTemplate
                    .Named("TestSimpleAlpha")
                    .WithTags(TestTags.Alpha)
                    .WithComponent<TestInt>(default(TestInt), variableUpdateOnly: true)
            );

            NAssert.AreNotEqual(
                envA.World.SchemaFingerprint.GroupsHash,
                envB.World.SchemaFingerprint.GroupsHash
            );
        }

        [Test]
        public void Fingerprint_SameSizeFieldReorder_ChangesGroupsHash()
        {
            // The gap this closes: TestLayoutIntFloat and TestLayoutFloatInt have
            // identical size (8 bytes) but reordered fields, so the size-only hash
            // can't tell them apart — only the source-gen-emitted layout hash can.
            // Two templates that differ solely by which of the pair they declare
            // must therefore produce different GroupsHashes, so a snapshot taken
            // before such an edit fails loudly instead of blitting bytes into the
            // wrong fields.
            NAssert.AreEqual(
                UnsafeUtility.SizeOf<TestLayoutIntFloat>(),
                UnsafeUtility.SizeOf<TestLayoutFloatInt>(),
                "Precondition: the pair must be the same blit width for this to "
                    + "exercise the layout hash rather than the size hash."
            );

            using var envA = EcsTestHelper.CreateEnvironment(
                TestTemplate
                    .Named("TestLayoutTemplate")
                    .WithTags(TestTags.Alpha)
                    .WithComponent<TestLayoutIntFloat>(default(TestLayoutIntFloat))
            );
            using var envB = EcsTestHelper.CreateEnvironment(
                TestTemplate
                    .Named("TestLayoutTemplate")
                    .WithTags(TestTags.Alpha)
                    .WithComponent<TestLayoutFloatInt>(default(TestLayoutFloatInt))
            );

            NAssert.AreNotEqual(
                envA.World.SchemaFingerprint.GroupsHash,
                envB.World.SchemaFingerprint.GroupsHash
            );
        }

        [Test]
        public void LayoutHashConst_IsEmittedAndDistinguishesReorder()
        {
            // The runtime calculator reads this generated const by name via
            // reflection; assert both that it exists (so source-gen ran) and that
            // it differs for the same-size reordered pair (so it actually carries
            // layout information). Mirrors WorldSchemaFingerprintCalculator's probe.
            const string fieldName = "__TrecsComponentLayoutHash";

            ulong Read<T>()
                where T : unmanaged, IEntityComponent
            {
                var field = typeof(T).GetField(
                    fieldName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
                );
                NAssert.IsNotNull(
                    field,
                    $"{typeof(T).Name} is missing the generated {fieldName} const — "
                        + "did the source generator run?"
                );
                NAssert.IsTrue(field.IsLiteral, $"{fieldName} should be a const.");
                return (ulong)field.GetRawConstantValue();
            }

            NAssert.AreNotEqual(Read<TestLayoutIntFloat>(), Read<TestLayoutFloatInt>());
        }

        [Test]
        public void Fingerprint_DifferentSystems_AreEqual()
        {
            // The system list is deliberately NOT part of the fingerprint:
            // paused state serializes by system identity (sparse), so adding/
            // removing/reordering systems keeps old snapshots loadable.
            // Behavioral divergence from changed simulation code is a replay
            // concern, surfaced by desync checksums at runtime — not a load
            // rejection.
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(new SchemaFingerprintTestSystemA()),
                TestTemplates.SimpleAlpha
            );

            NAssert.AreEqual(envA.World.SchemaFingerprint, envB.World.SchemaFingerprint);
        }

        [Test]
        public void Fingerprint_CustomSerializerRegistration_ChangesOnlyCustomSerializersHash()
        {
            using var envPlain = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envCustom = EcsTestHelper.CreateEnvironment(
                b => b.RegisterComponentArraySerializer(new InertComponentSerializer<TestInt>()),
                TestTemplates.SimpleAlpha
            );

            var plain = envPlain.World.SchemaFingerprint;
            var custom = envCustom.World.SchemaFingerprint;

            NAssert.AreNotEqual(plain.CustomSerializersHash, custom.CustomSerializersHash);
            NAssert.AreEqual(plain.GroupsHash, custom.GroupsHash);
            NAssert.AreEqual(plain.SetsHash, custom.SetsHash);
        }

        [Test]
        public void Fingerprint_CustomSerializerForTypeOutsideSchema_DoesNotChange()
        {
            // A serializer registered for a component type that no template
            // declares never affects the wire (no such component array
            // exists), so it must not affect the fingerprint either —
            // otherwise it would manufacture false incompatibilities.
            using var envPlain = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envCustom = EcsTestHelper.CreateEnvironment(
                b => b.RegisterComponentArraySerializer(new InertComponentSerializer<TestShort>()),
                TestTemplates.SimpleAlpha
            );

            NAssert.AreEqual(envPlain.World.SchemaFingerprint, envCustom.World.SchemaFingerprint);
        }

        [Test]
        public void Registry_RegisterAfterInitialize_Throws()
        {
            // Registrations are part of the world schema: mutating the
            // registry mid-session would change the snapshot wire format and
            // strand any snapshot taken earlier in the session (e.g. rewind
            // keyframes). The registry seals at World.Initialize.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);

            NAssert.Throws<TrecsException>(() =>
                env.World.ComponentArraySerializerRegistry.Register(
                    new InertComponentSerializer<TestInt>()
                )
            );
            NAssert.Throws<TrecsException>(() =>
                env.World.ComponentArraySerializerRegistry.Unregister<TestInt>()
            );
        }

        [Test]
        public void Snapshot_MetadataCarriesFingerprint_AndPeekExposesIt()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            env.World.Submit();

            var snapshots = CreateSnapshotSerializer(env.World);
            var data = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: data, includeTypeChecks: true);

            var peeked = snapshots.PeekMetadata(data);
            NAssert.AreEqual(env.World.SchemaFingerprint, peeked.SchemaFingerprint);
        }

        [Test]
        public void Snapshot_SameSchemaDifferentWorldInstance_Loads()
        {
            // The compatibility contract is schema equality, not world
            // identity: a snapshot saved by one process/world must load into
            // a freshly built world with the same schema.
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            envA.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            envA.World.Submit();

            var snapshotsA = CreateSnapshotSerializer(envA.World);
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            using var envB = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var snapshotsB = CreateSnapshotSerializer(envB.World);
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            NAssert.AreEqual(1, envB.Accessor.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Snapshot_SchemaMismatch_ThrowsWithExplanationBeforeWorldStateRead()
        {
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            envA.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            envA.World.Submit();

            var snapshotsA = CreateSnapshotSerializer(envA.World);
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            // "Code changed since the snapshot was saved": the template
            // gained a component.
            using var envB = EcsTestHelper.CreateEnvironment(
                TestTemplate
                    .Named("TestSimpleAlpha")
                    .WithTags(TestTags.Alpha)
                    .WithComponent<TestInt>(default(TestInt))
                    .WithComponent<TestFloat>(default(TestFloat))
            );
            var snapshotsB = CreateSnapshotSerializer(envB.World);

            stream.Position = 0;
            var ex = NAssert.Throws<SerializationException>(() => snapshotsB.LoadSnapshot(stream));

            StringAssert.Contains("different world schema", ex.Message);
            StringAssert.Contains("Groups/components", ex.Message);
            // The unrelated sections should not be blamed. (Match the bullet
            // prefix — bare "Sets:" also appears in the hex dump of the two
            // fingerprints.)
            StringAssert.DoesNotContain(" - Entity sets:", ex.Message);
        }

        [Test]
        public void Replay_SchemaMismatch_ThrowsBeforeWorldMutation()
        {
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            envA.Accessor.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 42 })
                .AssertComplete();
            envA.World.Submit();

            var registryA = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registryA);
            var worldStateSerA = new WorldStateSerializer(envA.World);
            var snapshotsA = new SnapshotSerializer(envA.World, registryA, worldStateSerA);
            var settings = new BundleRecorderSettings
            {
                Version = 1,
                KeyframeIntervalSeconds = 1000f,
            };
            using var recorder = new BundleRecorder(envA.World, registryA, settings, snapshotsA);
            recorder.Start();
            var bundle = recorder.Stop();

            // Replay against a world whose schema differs.
            using var envB = EcsTestHelper.CreateEnvironment(
                TestTemplate
                    .Named("TestSimpleAlpha")
                    .WithTags(TestTags.Alpha)
                    .WithComponent<TestInt>(default(TestInt))
                    .WithComponent<TestFloat>(default(TestFloat))
            );
            envB.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 5 }).AssertComplete();
            envB.World.Submit();

            var registryB = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registryB);
            var worldStateSerB = new WorldStateSerializer(envB.World);
            var snapshotsB = new SnapshotSerializer(envB.World, registryB, worldStateSerB);
            using var replayer = new BundleReplayer(envB.World, registryB, snapshotsB);

            var ex = NAssert.Throws<SerializationException>(() => replayer.Start(bundle));
            StringAssert.Contains("different world schema", ex.Message);

            // The gate fires before any world mutation: envB's state is intact.
            NAssert.AreEqual(1, envB.Accessor.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(BundlePlaybackState.Idle, replayer.State);
        }

        [Test]
        public void Bundle_HeaderFingerprint_RoundTripsThroughSaveLoad()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var settings = new BundleRecorderSettings
            {
                Version = 1,
                KeyframeIntervalSeconds = 1000f,
            };
            using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);
            recorder.Start();
            var bundle = recorder.Stop();

            NAssert.AreEqual(env.World.SchemaFingerprint, bundle.Header.SchemaFingerprint);

            var bundleSerializer = new RecordingBundleSerializer(registry);
            using var stream = new MemoryStream();
            bundleSerializer.Save(bundle, stream);
            stream.Position = 0;
            var peekedHeader = bundleSerializer.PeekHeader(stream);

            NAssert.AreEqual(env.World.SchemaFingerprint, peekedHeader.SchemaFingerprint);
        }
    }
}
