using System;
using System.IO;
using NUnit.Framework;
using Trecs.Internal;
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

            var registry = new SerializerRegistry();
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
        public void Snapshot_Twice_ProducesIdenticalBytes()
        {
            // World-level byte determinism: saving twice from the same world state must
            // produce identical byte streams. Required for rollback / desync detection
            // that hashes snapshot bytes. Complements the chunk-store-level
            // Serialize_Twice_ProducesIdenticalBytes test by also exercising component
            // arrays, entity handles map, sets, and the other heaps.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 11 }).AssertComplete();
            // Toss in a native-unique allocation so the chunk-store path is exercised.
            var ptr = NativeUniquePtr.Alloc<int>(a.Heap, 42);
            a.SubmitEntities();

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var s1 = new MemoryStream();
            snapshots.SaveSnapshot(version: 1, stream: s1);
            using var s2 = new MemoryStream();
            snapshots.SaveSnapshot(version: 1, stream: s2);

            CollectionAssert.AreEqual(
                s1.ToArray(),
                s2.ToArray(),
                "Identical world state must produce identical snapshot bytes"
            );

            ptr.Dispose(a);
        }

        [Test]
        public void Snapshot_PeekMetadata_DoesNotMutateWorld()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            var registry = new SerializerRegistry();
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

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            var path = Path.Combine(Path.GetTempPath(), $"trecs_test_{Guid.NewGuid():N}.snap");
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
            var registry = new SerializerRegistry();

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
            var registry = new SerializerRegistry();
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
            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var empty = new MemoryStream();
            NAssert.Throws<SerializationException>(() => snapshots.LoadSnapshot(empty));
        }

        [Test]
        public void Snapshot_ArgumentValidation()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = new SerializerRegistry();
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
                    Path.Combine(Path.GetTempPath(), $"trecs_nonexistent_{Guid.NewGuid():N}.snap")
                )
            );
        }

        [Test]
        public void Snapshot_PostDispose_Throws_ObjectDisposedException()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = new SerializerRegistry();
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

        [Test]
        public void Snapshot_PreservesNativeUniquePtrHandleAndData()
        {
            // Handles must round-trip across save/load: components storing a
            // NativeUniquePtr handle keep resolving after a snapshot restore.
            // The chunk-store dump preserves slot/generation; the heap restores its
            // managed-side handle→type bookkeeping.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var ptr = NativeUniquePtr.Alloc<int>(a.Heap, 42);
            a.SubmitEntities();
            var savedHandle = ptr.Handle;

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshot(version: 1, stream: stream);

            // Mutate after save: dispose the original, allocate something else so the heap's
            // state is genuinely different at load time.
            ptr.Dispose(a);
            a.SubmitEntities();
            var sideEffect = NativeUniquePtr.Alloc<int>(a.Heap, 99);
            a.SubmitEntities();
            NAssert.AreEqual(99, sideEffect.Read(a.Heap).Value);

            // Load — should restore the original handle exactly.
            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new NativeUniquePtr<int>(savedHandle);
            NAssert.AreEqual(42, restored.Read(a.Heap).Value);
        }

        [Test]
        public void Snapshot_PreservesEmptyTrecsList()
        {
            // Capacity=0 lists have header->DataHandle == null and header->Data ==
            // IntPtr.Zero. The Deserialize path must handle that without trying to
            // resolve the null DataHandle, and the restored list must read back as
            // an empty list (not throw, not allocate a buffer).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var list = TrecsList.Alloc<int>(a.Heap);
            a.SubmitEntities();
            var savedHandle = list.Handle;

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshot(version: 1, stream: stream);

            list.Dispose(a);
            a.SubmitEntities();

            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new TrecsList<int>(savedHandle);
            var read = restored.Read(a.Heap);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(0, read.Capacity);

            // Sanity: a follow-up EnsureCapacity + Add still works against the restored
            // empty list (no stale data pointer hangover).
            restored.EnsureCapacity(a.Heap, 4);
            restored.Write(a.Heap).Add(99);
            NAssert.AreEqual(99, restored.Read(a.Heap)[0]);

            restored.Dispose(a);
        }

        [Test]
        public void Snapshot_PreservesTrecsListHandleAndContents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var list = TrecsList.Alloc<int>(a.Heap, 8);
            var w = list.Write(a.Heap);
            w.Add(11);
            w.Add(22);
            w.Add(33);
            a.SubmitEntities();
            var savedHandle = list.Handle;

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshot(version: 1, stream: stream);

            // Mutate after save.
            list.Dispose(a);
            a.SubmitEntities();

            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new TrecsList<int>(savedHandle);
            var read = restored.Read(a.Heap);
            NAssert.AreEqual(3, read.Count);
            NAssert.AreEqual(11, read[0]);
            NAssert.AreEqual(22, read[1]);
            NAssert.AreEqual(33, read[2]);
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
