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
            a.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            var savedMetadata = snapshots.SaveSnapshotToStream(version: 1, stream: stream);
            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));

            // Mutate after saving so we can verify the load reverts state
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 0 }).AssertComplete();
            a.World.Submit();
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
            var ptr = NativeUniquePtr.Alloc<int>(a, 42);
            a.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var s1 = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: s1);
            using var s2 = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: s2);

            CollectionAssert.AreEqual(
                s1.ToArray(),
                s2.ToArray(),
                "Identical world state must produce identical snapshot bytes"
            );

            ptr.Dispose(a);
        }

        [Test]
        public void Snapshot_RoundTripsViaSerializationData()
        {
            // Mirror of Snapshot_RoundTripsEntityState but through the zero-copy
            // SerializationData save/load path used by the rewind/recording retain path.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 11 }).AssertComplete();
            a.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            var data = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: data, includeTypeChecks: true);
            var savedMetadata = snapshots.PeekMetadata(data);
            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));

            // Mutate after saving so we can verify the load reverts state.
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 0 }).AssertComplete();
            a.World.Submit();
            NAssert.AreEqual(3, a.CountEntitiesWithTags(TestTags.Alpha));

            var loadedMetadata = snapshots.LoadSnapshot(data);

            NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            NAssert.AreEqual(savedMetadata.FixedFrame, loadedMetadata.FixedFrame);
        }

        [Test]
        public void Snapshot_AfterBlobChurn_ReflectsNewBlobSet()
        {
            // Regression guard for PrepareMetadata's stamped-BlobIds rebuild-skip: heap blob
            // membership changes between saves must invalidate the stamp (via the heaps'
            // BlobMembershipVersion bumps), so consecutive saves reflect the live set rather
            // than reusing the previous save's.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.World.Submit();

            var blobId1 = new BlobId(101);
            NativeSharedAnchor.Register(a, blobId1, 7);
            var p1 = NativeSharedPtr.Acquire<int>(a, blobId1);

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            var d1 = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: d1, includeTypeChecks: false);
            var m1 = snapshots.PeekMetadata(d1);
            NAssert.IsTrue(m1.BlobIds.Contains(blobId1));

            // Membership grows while the previous save's stamp is still warm.
            var blobId2 = new BlobId(202);
            NativeSharedAnchor.Register(a, blobId2, 9);
            var p2 = NativeSharedPtr.Acquire<int>(a, blobId2);

            var d2 = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: d2, includeTypeChecks: false);
            var m2 = snapshots.PeekMetadata(d2);
            NAssert.IsTrue(m2.BlobIds.Contains(blobId1));
            NAssert.IsTrue(m2.BlobIds.Contains(blobId2));

            // Membership shrinks again.
            p2.Dispose(a);

            var d3 = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: d3, includeTypeChecks: false);
            var m3 = snapshots.PeekMetadata(d3);
            NAssert.IsTrue(m3.BlobIds.Contains(blobId1));
            NAssert.IsFalse(m3.BlobIds.Contains(blobId2));

            p1.Dispose(a);
        }

        [Test]
        public void Snapshot_SaveAfterLoadIntoSameScratch_ProducesIdenticalBytes()
        {
            // The rollback loop's exact metadata flow: serialize and deserialize share one
            // scratch metadata instance, so the load re-stamps it (post-load heaps hold exactly
            // the snapshot's blobs) and the following save may skip the referenced-blob rebuild.
            // The re-serialized bytes must still be identical to the original snapshot.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.World.Submit();

            var blobId1 = new BlobId(101);
            NativeSharedAnchor.Register(a, blobId1, 7);
            var p1 = NativeSharedPtr.Acquire<int>(a, blobId1);

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var scratch = new SnapshotSerializerScratch();

            var d1 = new SerializationData();
            snapshots.Serialize(
                version: 1,
                includeTypeChecks: false,
                target: d1,
                scratch: scratch,
                opaqueBlobIdsOut: null,
                requireOpaqueHandling: true
            );

            // Post-snapshot churn the load must revert (and whose membership bump the
            // stamped set must survive being re-validated against).
            var blobId2 = new BlobId(202);
            NativeSharedAnchor.Register(a, blobId2, 9);
            NativeSharedPtr.Acquire<int>(a, blobId2);

            snapshots.Deserialize(d1, scratch.Metadata);

            var d2 = new SerializationData();
            snapshots.Serialize(
                version: 1,
                includeTypeChecks: false,
                target: d2,
                scratch: scratch,
                opaqueBlobIdsOut: null,
                requireOpaqueHandling: true
            );

            CollectionAssert.AreEqual(
                ToContiguousBytes(d1),
                ToContiguousBytes(d2),
                "save-after-load must reproduce the loaded snapshot byte-for-byte"
            );

            p1.Dispose(a);
        }

        static byte[] ToContiguousBytes(SerializationData data)
        {
            using var ms = new MemoryStream();
            data.WriteContiguousTo(ms);
            return ms.ToArray();
        }

        [Test]
        public void Snapshot_DeserializeListenerAcquiresBlob_NextSaveReflectsIt()
        {
            // The stamp-staleness hazard: DeserializeCompleted listeners run AFTER the heaps
            // section loads, so a listener that mutates blob membership must leave the
            // load-time stamp stale — the next save with the shared scratch has to rebuild
            // its referenced-blob set rather than reuse the (now wrong) wire set. Guards the
            // heaps-boundary version capture in WorldStateSerializer.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.World.Submit();

            var blobId1 = new BlobId(101);
            NativeSharedAnchor.Register(a, blobId1, 7);
            var p1 = NativeSharedPtr.Acquire<int>(a, blobId1);

            var blobId2 = new BlobId(202);
            NativeSharedAnchor.Register(a, blobId2, 9);

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var scratch = new SnapshotSerializerScratch();

            var d1 = new SerializationData();
            snapshots.Serialize(
                version: 1,
                includeTypeChecks: false,
                target: d1,
                scratch: scratch,
                opaqueBlobIdsOut: null,
                requireOpaqueHandling: true
            );

            // Listener acquires blob 2 after every load — post-heaps-section membership churn.
            NativeSharedPtr<int> p2 = default;
            using var sub = a.Events.OnDeserializeCompleted(() =>
            {
                p2 = NativeSharedPtr.Acquire<int>(a, blobId2);
            });

            snapshots.Deserialize(d1, scratch.Metadata);

            var d2 = new SerializationData();
            snapshots.Serialize(
                version: 1,
                includeTypeChecks: false,
                target: d2,
                scratch: scratch,
                opaqueBlobIdsOut: null,
                requireOpaqueHandling: true
            );

            var m2 = snapshots.PeekMetadata(d2);
            NAssert.IsTrue(m2.BlobIds.Contains(blobId1));
            NAssert.IsTrue(
                m2.BlobIds.Contains(blobId2),
                "save-after-load must include the blob the DeserializeCompleted listener "
                    + "acquired — the load-time stamp must not survive post-load membership churn"
            );

            p2.Dispose(a);
            p1.Dispose(a);
        }

        [Test]
        public void Snapshot_SerializationDataChecksum_MatchesContiguousVerifyChecksum()
        {
            // Correctness-critical for desync detection: the retain-path checksum (computed
            // in place over the two sections) MUST equal the checksum the contiguous verify
            // path (ComputeChecksum) recomputes for identical state — otherwise every frame
            // would false-positive as a desync.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 11 }).AssertComplete();
            var ptr = NativeUniquePtr.Alloc<int>(a, 42);
            a.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            var data = new SerializationData();
            snapshots.SaveSnapshot(version: 1, target: data, includeTypeChecks: true);
            ulong dataChecksum = data.ComputeContiguousChecksum();

            ulong verifyChecksum = snapshots.ComputeChecksum(version: 1, includeTypeChecks: true);

            NAssert.AreEqual(
                verifyChecksum,
                dataChecksum,
                "retain-path checksum must equal the contiguous verify-path checksum"
            );

            ptr.Dispose(a);
        }

        [Test]
        public void Snapshot_PeekMetadata_DoesNotMutateWorld()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            a.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            var saved = snapshots.SaveSnapshotToStream(version: 1, stream: stream);

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
            a.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            var path = Path.Combine(Path.GetTempPath(), $"trecs_test_{Guid.NewGuid():N}.snap");
            try
            {
                snapshots.SaveSnapshotToFile(version: 1, filePath: path);
                NAssert.IsTrue(File.Exists(path));

                a.Query()
                    .WithTags(TestTags.Alpha)
                    .SingleHandle()
                    .Component<TestInt>(a)
                    .Write.Value = 0;
                NAssert.AreEqual(
                    0,
                    a.Query()
                        .WithTags(TestTags.Alpha)
                        .SingleHandle()
                        .Component<TestInt>(a)
                        .Read.Value
                );

                snapshots.LoadSnapshot(path);

                NAssert.AreEqual(
                    13,
                    a.Query()
                        .WithTags(TestTags.Alpha)
                        .SingleHandle()
                        .Component<TestInt>(a)
                        .Read.Value
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
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);

            // CustomMarker is not a Trecs component; we just round-trip an instance
            // through the serializer to prove the registration path works.
            registry.RegisterSerializer(new CustomMarkerSerializer());

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();

            var original = new CustomMarker { Tag = 0xCAFE, Counter = 7 };
            helper.WriteAll(data, original, version: 1, includeTypeChecks: true);

            var roundTripped = helper.ReadAll<CustomMarker>(data);

            NAssert.AreEqual(original.Tag, roundTripped.Tag);
            NAssert.AreEqual(original.Counter, roundTripped.Counter);
        }

        [Test]
        public void Snapshot_VersionIsPreservedInMetadata()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            var saved = snapshots.SaveSnapshotToStream(version: 42, stream: stream);

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
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var empty = new MemoryStream();
            NAssert.Throws<SerializationException>(() => snapshots.LoadSnapshot(empty));
        }

        [Test]
        public void Snapshot_ArgumentValidation()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            NAssert.Throws<ArgumentNullException>(() =>
                snapshots.SaveSnapshot(
                    version: 1,
                    target: (SerializationData)null,
                    includeTypeChecks: false
                )
            );
            NAssert.Throws<ArgumentNullException>(() => snapshots.LoadSnapshot((Stream)null));
            NAssert.Throws<FileNotFoundException>(() =>
                snapshots.LoadSnapshot(
                    Path.Combine(Path.GetTempPath(), $"trecs_nonexistent_{Guid.NewGuid():N}.snap")
                )
            );
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

            var ptr = NativeUniquePtr.Alloc<int>(a, 42);
            a.World.Submit();
            var savedHandle = ptr.Handle;

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: stream);

            // Mutate after save: dispose the original, allocate something else so the heap's
            // state is genuinely different at load time.
            ptr.Dispose(a);
            a.World.Submit();
            var sideEffect = NativeUniquePtr.Alloc<int>(a, 99);
            a.World.Submit();
            NAssert.AreEqual(99, sideEffect.Read(a).Value);

            // Load — should restore the original handle exactly.
            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new NativeUniquePtr<int>(savedHandle);
            NAssert.AreEqual(42, restored.Read(a).Value);
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

            var list = TrecsList.Alloc<int>(a);
            a.World.Submit();
            var savedHandle = list.Handle;

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: stream);

            list.Dispose(a);
            a.World.Submit();

            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new TrecsList<int>(savedHandle);
            var read = restored.Read(a);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(0, read.Capacity);

            // Sanity: a follow-up EnsureCapacity + Add still works against the restored
            // empty list (no stale data pointer hangover).
            restored.EnsureCapacity(a, 4);
            restored.Write(a).Add(99);
            NAssert.AreEqual(99, restored.Read(a)[0]);

            restored.Dispose(a);
        }

        [Test]
        public void Snapshot_PreservesTrecsListHandleAndContents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var list = TrecsList.Alloc<int>(a, 8);
            var w = list.Write(a);
            w.Add(11);
            w.Add(22);
            w.Add(33);
            a.World.Submit();
            var savedHandle = list.Handle;

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: stream);

            // Mutate after save.
            list.Dispose(a);
            a.World.Submit();

            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new TrecsList<int>(savedHandle);
            var read = restored.Read(a);
            NAssert.AreEqual(3, read.Count);
            NAssert.AreEqual(11, read[0]);
            NAssert.AreEqual(22, read[1]);
            NAssert.AreEqual(33, read[2]);
        }

        [Test]
        public void Snapshot_PreservesTrecsDictionaryHandleAndContents()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var dict = TrecsDictionary.Alloc<int, float>(a, 8);
            var w = dict.Write(a);
            w.Add(100, 1.5f);
            w.Add(200, 2.5f);
            w.Add(300, 3.5f);
            a.World.Submit();
            var savedHandle = dict.Handle;

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: stream);

            // Mutate after save.
            dict.Dispose(a);
            a.World.Submit();

            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new TrecsDictionary<int, float>(savedHandle);
            var read = restored.Read(a);
            NAssert.AreEqual(3, read.Count);

            // Verify keys findable and values correct
            NAssert.IsTrue(read.TryGetValue(100, out var v1));
            NAssert.AreEqual(1.5f, v1);
            NAssert.IsTrue(read.TryGetValue(200, out var v2));
            NAssert.AreEqual(2.5f, v2);
            NAssert.IsTrue(read.TryGetValue(300, out var v3));
            NAssert.AreEqual(3.5f, v3);
            NAssert.IsFalse(read.ContainsKey(999));

            // Verify iteration order preserved
            int idx = 0;
            int[] expectedKeys = { 100, 200, 300 };
            float[] expectedValues = { 1.5f, 2.5f, 3.5f };
            foreach (var (key, value) in read)
            {
                NAssert.AreEqual(expectedKeys[idx], key);
                NAssert.AreEqual(expectedValues[idx], value);
                idx++;
            }
            NAssert.AreEqual(3, idx);
        }

        [Test]
        public void Snapshot_PreservesEmptyTrecsDictionary()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var dict = TrecsDictionary.Alloc<int, float>(a);
            a.World.Submit();
            var savedHandle = dict.Handle;

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

            using var stream = new MemoryStream();
            snapshots.SaveSnapshotToStream(version: 1, stream: stream);

            dict.Dispose(a);
            a.World.Submit();

            stream.Position = 0;
            snapshots.LoadSnapshot(stream);

            var restored = new TrecsDictionary<int, float>(savedHandle);
            var read = restored.Read(a);
            NAssert.AreEqual(0, read.Count);

            // Sanity: a follow-up EnsureCapacity + Add still works against the restored
            // empty dictionary (no stale data pointer hangover).
            restored.EnsureCapacity(a, 4);
            var rw = restored.Write(a);
            rw.Add(42, 99.0f);
            NAssert.AreEqual(99.0f, restored.Read(a)[42]);

            restored.Dispose(a);
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
