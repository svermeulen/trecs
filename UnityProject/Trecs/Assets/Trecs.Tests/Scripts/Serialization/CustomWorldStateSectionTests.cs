using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for <see cref="ICustomWorldStateSection"/>: game-defined
    /// sections appended to the world-state stream. Covers snapshot
    /// round-trips, checksum coverage, the registration seal, and the
    /// schema-fingerprint gate that rejects payloads whose section set
    /// differs from the live world's.
    /// </summary>
    [TestFixture]
    public class CustomWorldStateSectionTests
    {
        class TestStateSection : ICustomWorldStateSection
        {
            public int Value;
            public int SerializeCalls;
            public int DeserializeCalls;

            public void Serialize(ISerializationWriter writer)
            {
                SerializeCalls++;
                writer.Write("Value", Value);
            }

            public void Deserialize(ISerializationReader reader)
            {
                DeserializeCalls++;
                Value = reader.Read<int>("Value");
            }
        }

        // A section whose Deserialize deliberately consumes less than its
        // Serialize wrote — the "wire drift inside one section" failure mode
        // the per-entry guard exists to catch.
        class DriftingStateSection : ICustomWorldStateSection
        {
            public void Serialize(ISerializationWriter writer)
            {
                writer.Write("Value", 0);
            }

            public void Deserialize(ISerializationReader reader)
            {
                // Reads nothing: the next guard read lands inside the int.
            }
        }

        static SnapshotSerializer CreateSnapshotSerializer(World world)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            return new SnapshotSerializer(world, registry, new WorldStateSerializer(world));
        }

        // Raw world-state stream helpers (no snapshot metadata, so no schema
        // fingerprint gate) — the path where ReadCustomSections' own
        // count/name/guard validation is load-bearing.
        static byte[] SerializeWorldRaw(World world)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var serializer = new WorldStateSerializer(world);
            var writer = new BinarySerializationWriter(registry);
            var serData = new SerializationData();
            writer.Start(serData, version: 1, includeTypeChecks: false);
            serializer.SerializeFullState(writer);
            writer.Complete();

            using var outputStream = new MemoryStream();
            serData.WriteContiguousTo(outputStream);
            return outputStream.ToArray();
        }

        static void DeserializeWorldRaw(World world, byte[] data)
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var serializer = new WorldStateSerializer(world);
            var reader = new BinarySerializationReader(registry);
            reader.Start(new ContiguousSerializationData(data));
            serializer.DeserializeState(reader);
        }

        [Test]
        public void Section_RoundTripsThroughSnapshot()
        {
            var section = new TestStateSection { Value = 17 };
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", section),
                TestTemplates.SimpleAlpha
            );

            var snapshots = CreateSnapshotSerializer(env.World);
            var data = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: data, includeTypeChecks: true);
            NAssert.AreEqual(1, section.SerializeCalls);

            // Mutate post-save state; the load must restore the saved value.
            section.Value = -1;
            snapshots.LoadSnapshot(data);

            NAssert.AreEqual(1, section.DeserializeCalls);
            NAssert.AreEqual(17, section.Value);
        }

        [Test]
        public void MultipleSections_RoundTripInRegistrationOrder()
        {
            var sectionA = new TestStateSection { Value = 1 };
            var sectionB = new TestStateSection { Value = 2 };
            using var env = EcsTestHelper.CreateEnvironment(
                b =>
                    b.RegisterCustomWorldStateSection("SectionA", sectionA)
                        .RegisterCustomWorldStateSection("SectionB", sectionB),
                TestTemplates.SimpleAlpha
            );

            var snapshots = CreateSnapshotSerializer(env.World);
            var data = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: data, includeTypeChecks: true);

            sectionA.Value = -1;
            sectionB.Value = -1;
            snapshots.LoadSnapshot(data);

            NAssert.AreEqual(1, sectionA.Value);
            NAssert.AreEqual(2, sectionB.Value);
        }

        [Test]
        public void Checksum_CoversSectionData()
        {
            var section = new TestStateSection { Value = 1 };
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", section),
                TestTemplates.SimpleAlpha
            );

            var snapshots = CreateSnapshotSerializer(env.World);
            var checksumBefore = snapshots.ComputeChecksum(version: 1, includeTypeChecks: false);
            section.Value = 2;
            var checksumAfter = snapshots.ComputeChecksum(version: 1, includeTypeChecks: false);

            // Desync detection must cover custom-section state: two worlds
            // that differ only in section data are different game states.
            NAssert.AreNotEqual(checksumBefore, checksumAfter);
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
                env.World.CustomWorldStateSections.Register("TooLate", new TestStateSection())
            );
        }

        [Test]
        public void Registry_DuplicateName_Throws()
        {
            NAssert.Throws<TrecsException>(() =>
                EcsTestHelper
                    .CreateEnvironment(
                        b =>
                            b.RegisterCustomWorldStateSection("Duplicate", new TestStateSection())
                                .RegisterCustomWorldStateSection(
                                    "Duplicate",
                                    new TestStateSection()
                                ),
                        TestTemplates.SimpleAlpha
                    )
                    .Dispose()
            );
        }

        [Test]
        public void Fingerprint_SectionRegistration_ChangesOnlyCustomSectionsHash()
        {
            using var envPlain = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            using var envSection = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );

            var plain = envPlain.World.SchemaFingerprint;
            var withSection = envSection.World.SchemaFingerprint;

            NAssert.AreNotEqual(plain.CustomSectionsHash, withSection.CustomSectionsHash);
            NAssert.AreEqual(plain.GroupsHash, withSection.GroupsHash);
            NAssert.AreEqual(plain.SetsHash, withSection.SetsHash);
            NAssert.AreEqual(plain.CustomSerializersHash, withSection.CustomSerializersHash);
        }

        [Test]
        public void Fingerprint_SameSectionNames_AreEqual()
        {
            // The fingerprint hashes section identity (names, in order) — not
            // the implementing instances — so two identically configured
            // worlds agree, which is what lets a snapshot travel between
            // processes.
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );
            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );

            NAssert.AreEqual(envA.World.SchemaFingerprint, envB.World.SchemaFingerprint);
        }

        [Test]
        public void Snapshot_SectionSetMismatch_ThrowsWithExplanation()
        {
            // Saved without the section; loaded into a world that has one
            // registered. The schema-fingerprint gate must reject it with the
            // custom-sections aspect named, before any world state is read.
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            envA.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            envA.World.Submit();

            var snapshotsA = CreateSnapshotSerializer(envA.World);
            var data = new SerializationData();
            snapshotsA.SaveSnapshot(version: 1, target: data, includeTypeChecks: true);

            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );
            var snapshotsB = CreateSnapshotSerializer(envB.World);

            var ex = NAssert.Throws<SerializationException>(() => snapshotsB.LoadSnapshot(data));

            StringAssert.Contains("different world schema", ex.Message);
            StringAssert.Contains("Custom world-state sections", ex.Message);
        }

        [Test]
        public void RawStream_SectionCountMismatch_Throws()
        {
            // Raw world-state streams carry no snapshot metadata, so the
            // schema-fingerprint gate never runs — ReadCustomSections' own
            // count check is the load-bearing release-build error path.
            using var envA = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var data = SerializeWorldRaw(envA.World);

            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("TestSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );

            var ex = NAssert.Throws<SerializationException>(() =>
                DeserializeWorldRaw(envB.World, data)
            );
            StringAssert.Contains("0 custom section(s)", ex.Message);
            StringAssert.Contains("1 registered", ex.Message);
        }

        [Test]
        public void RawStream_SectionNameMismatch_ThrowsNamingRegisteredSection()
        {
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("WrittenSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );
            var data = SerializeWorldRaw(envA.World);

            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("OtherSection", new TestStateSection()),
                TestTemplates.SimpleAlpha
            );

            var ex = NAssert.Throws<SerializationException>(() =>
                DeserializeWorldRaw(envB.World, data)
            );
            StringAssert.Contains("OtherSection", ex.Message);
        }

        [Test]
        public void RawStream_SectionPayloadDrift_ThrowsNamingSection()
        {
            // A section whose Deserialize under-consumes its own payload must
            // fail at its per-entry guard with the section named — not
            // cascade misaligned bytes into the next read.
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.RegisterCustomWorldStateSection("DriftSection", new DriftingStateSection()),
                TestTemplates.SimpleAlpha
            );
            var data = SerializeWorldRaw(env.World);

            var ex = NAssert.Throws<SerializationException>(() =>
                DeserializeWorldRaw(env.World, data)
            );
            StringAssert.Contains("DriftSection", ex.Message);
            StringAssert.Contains("stream drift", ex.Message);
        }
    }
}
