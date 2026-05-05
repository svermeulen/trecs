using System;
using System.IO;
using NUnit.Framework;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class SerializationRoundTripTests
    {
        [Test]
        public void Snapshot_RoundTripsEntityState()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 11 }).AssertComplete();
            a.SubmitEntities();

            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var stream = new MemoryStream();
            var savedMetadata = snapshots.SaveSnapshot(version: 1, stream: stream);
            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));

            // Mutate after saving so we can verify the load reverts state
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 0 }).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Alpha));

            stream.Position = 0;
            var loadedMetadata = snapshots.LoadSnapshot(stream);

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(savedMetadata.FixedFrame, loadedMetadata.FixedFrame);
        }

        [Test]
        public void Snapshot_PeekMetadata_DoesNotMutateWorld()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var stream = new MemoryStream();
            var saved = snapshots.SaveSnapshot(version: 1, stream: stream);

            stream.Position = 0;
            var peeked = snapshots.PeekMetadata(stream);

            NAssert.AreEqual(saved.FixedFrame, peeked.FixedFrame);
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        [Test]
        public void Snapshot_RoundTripsViaFile()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 13 }).AssertComplete();
            a.SubmitEntities();

            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            var path = Path.Combine(Path.GetTempPath(), $"trecs_test_{Guid.NewGuid():N}.bin");
            try
            {
                snapshots.SaveSnapshot(version: 1, filePath: path);
                NAssert.IsTrue(File.Exists(path));

                a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>().Write.Value = 0;
                NAssert.AreEqual(
                    0,
                    a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>().Read.Value
                );

                snapshots.LoadSnapshot(path);

                NAssert.AreEqual(
                    13,
                    a.Query().WithTags(TestTags.Alpha).Single().Get<TestInt>().Read.Value
                );
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void CustomSerializer_RegistersAndRoundTrips()
        {
            // A user-defined component type (already blittable) registered explicitly
            // via the generic Register<TSerializer>() path. Demonstrates the same shape
            // a non-blittable custom serializer would take, except writing more fields.
            var registry = TrecsSerialization.CreateSerializerRegistry();

            // CustomMarker is not a Trecs component; we just round-trip an instance
            // through SerializationBuffer to prove the registration path works.
            registry.RegisterSerializer<CustomMarkerSerializer>();

            using var buffer = new SerializationBuffer(registry);

            var original = new CustomMarker { Tag = 0xCAFE, Counter = 7 };
            buffer.WriteAll(original, version: 1, includeTypeChecks: true);
            buffer.ResetMemoryPosition();

            var roundTripped = buffer.ReadAll<CustomMarker>();

            NAssert.AreEqual(original.Tag, roundTripped.Tag);
            NAssert.AreEqual(original.Counter, roundTripped.Counter);
        }

        [Test]
        public void Snapshot_VersionIsPreservedInMetadata()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var stream = new MemoryStream();
            var saved = snapshots.SaveSnapshot(version: 42, stream: stream);

            NAssert.AreEqual(42, saved.Version);

            stream.Position = 0;
            var peeked = snapshots.PeekMetadata(stream);
            NAssert.AreEqual(42, peeked.Version);

            stream.Position = 0;
            var loaded = snapshots.LoadSnapshot(stream);
            NAssert.AreEqual(42, loaded.Version);
        }

        [Test]
        public void Snapshot_LoadingEmptyStream_Throws_SerializationException()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var empty = new MemoryStream();
            NAssert.Throws<SerializationException>(() => snapshots.LoadSnapshot(empty));
        }

        [Test]
        public void Snapshot_ArgumentValidation()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            NAssert.Throws<ArgumentNullException>(() =>
                snapshots.SaveSnapshot(version: 1, stream: (Stream)null)
            );
            NAssert.Throws<ArgumentException>(() =>
                snapshots.SaveSnapshot(version: 1, filePath: "")
            );
            NAssert.Throws<ArgumentNullException>(() => snapshots.LoadSnapshot((Stream)null));
            NAssert.Throws<FileNotFoundException>(() =>
                snapshots.LoadSnapshot(
                    Path.Combine(Path.GetTempPath(), $"trecs_nonexistent_{Guid.NewGuid():N}.bin")
                )
            );
        }

        [Test]
        public void Snapshot_PostDispose_Throws_ObjectDisposedException()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = TrecsSerialization.CreateSerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            snapshots.Dispose();

            NAssert.Throws<ObjectDisposedException>(() =>
                snapshots.SaveSnapshot(version: 1, stream: new MemoryStream())
            );
            NAssert.Throws<ObjectDisposedException>(() =>
                snapshots.LoadSnapshot(new MemoryStream(new byte[] { 0 }))
            );

            // Idempotent Dispose.
            snapshots.Dispose();
        }

        struct CustomMarker
        {
            public int Tag;
            public int Counter;
        }

        class CustomMarkerSerializer : ISerializer<CustomMarker>
        {
            public void Serialize(in CustomMarker value, ISerializationWriter writer)
            {
                writer.Write("Tag", value.Tag);
                writer.Write("Counter", value.Counter);
            }

            public void Deserialize(ref CustomMarker value, ISerializationReader reader)
            {
                value.Tag = reader.Read<int>("Tag");
                value.Counter = reader.Read<int>("Counter");
            }
        }
    }
}
