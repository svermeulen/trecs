using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for the descriptor journal: derivable (interner-backed) blobs referenced by a snapshot
    /// persist their descriptors so a fresh-process load can re-derive them without re-storing the
    /// blob bytes. Covers the interner collect/restore mechanism, the <see cref="SnapshotMetadata"/>
    /// serialization round-trip, and an end-to-end two-world snapshot where the loading world never
    /// interned the descriptor itself.
    /// </summary>
    [TestFixture]
    public class BlobJournalTests
    {
        readonly struct Descriptor
        {
            public readonly int Seed;

            public Descriptor(int seed)
            {
                Seed = seed;
            }
        }

        readonly struct Blob
        {
            public readonly int Value;

            public Blob(int value)
            {
                Value = value;
            }
        }

        static SerializerRegistry CreateRegistry()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            registry.RegisterSerializer(new BlitSerializer<Descriptor>());
            return registry;
        }

        [Test]
        public void Interner_CollectThenRestore_ReDerivesInFreshCache()
        {
            var registry = CreateRegistry();

            using var poolA = new NativeBlobBoxPool();
            using var cacheA = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolA);
            var internerA = new BlobFactory(TrecsLog.Default, cacheA, registry);
            internerA.AddFactory<Descriptor>(
                new NativeDescriptorBlobFactory<Descriptor, Blob>(d => new Blob(d.Seed * d.Seed))
            );

            var id = internerA.Intern(new Descriptor(3));
            var ptrA = internerA.AcquireNativeSharedAnchor<Blob>(id); // pin so it's "active"

            var activeIds = new IterableHashSet<BlobId>();
            cacheA.GetAllActiveBlobIds(activeIds);
            var journal = new IterableDictionary<BlobId, object>();
            internerA.CollectJournaledDescriptors(activeIds, journal);

            NAssert.AreEqual(1, journal.Count);
            NAssert.IsTrue(journal.ContainsKey(id));

            // A fresh cache + interner: the factory is registered, but the descriptor was never
            // interned here, so the source does not exist until the journal restores it.
            using var poolB = new NativeBlobBoxPool();
            using var cacheB = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolB);
            var internerB = new BlobFactory(TrecsLog.Default, cacheB, registry);
            internerB.AddFactory<Descriptor>(
                new NativeDescriptorBlobFactory<Descriptor, Blob>(d => new Blob(d.Seed * d.Seed))
            );

            NAssert.IsFalse(internerB.IsRegistered(id));

            internerB.RestoreJournaledDescriptors(journal);

            NAssert.IsTrue(internerB.IsRegistered(id));
            var ptrB = internerB.AcquireNativeSharedAnchor<Blob>(id);
            NAssert.AreEqual(9, cacheB.GetNativeBlobRef<Blob>(id).Value);

            ptrB.Dispose(cacheB);
            ptrA.Dispose(cacheA);
            internerA.Dispose();
            internerB.Dispose();
        }

        // ─── Input-descriptor sweep (the journal/source GC for input interns) ───

        // Shared harness for the sweep tests: a managed input heap over a fresh cache + factory.
        static (BlobCache cache, BlobFactory interner, InputSharedHeap heap) CreateInputHarness(
            NativeBlobBoxPool pool
        )
        {
            var registry = CreateRegistry();
            var cache = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, pool);
            var interner = new BlobFactory(TrecsLog.Default, cache, registry);
            interner.AddFactory<Descriptor>(
                new ManagedDescriptorBlobFactory<Descriptor, string>(d => $"seed-{d.Seed}")
            );
            var heap = new InputSharedHeap(TrecsLog.Default, cache, interner);
            return (cache, interner, heap);
        }

        static void Sweep(BlobFactory interner, InputSharedHeap heap)
        {
            var liveIds = new IterableHashSet<BlobId>();
            heap.AddReferencedBlobIds(liveIds);
            interner.SweepInputDescriptors(liveIds);
        }

        [Test]
        public void InputDescriptorSweep_UnreferencedUnpromoted_ForgetsSourceAndBytes()
        {
            // The core forget: an input-descriptor blob whose last frame trimmed (and which the sim
            // never justified) loses its source and bytes entirely — by-id re-acquire misses where
            // the previously-immortal source used to satisfy it.
            using var pool = new NativeBlobBoxPool();
            var (cache, interner, heap) = CreateInputHarness(pool);
            using (cache)
            {
                var ptr = heap.AcquireFromDescriptor<Descriptor, string>(
                    frame: 5,
                    new Descriptor(3)
                );
                NAssert.IsTrue(interner.IsRegistered(ptr.BlobId));

                heap.ClearAtOrBeforeFrame(5);
                Sweep(interner, heap);

                NAssert.IsFalse(interner.IsRegistered(ptr.BlobId));
                NAssert.IsFalse(cache.IsResident(ptr.BlobId));
                NAssert.IsFalse(heap.TryAcquire<string>(frame: 6, ptr.BlobId, out _));

                // DEBUG VerifyLruConsistency: the targeted evict kept the inactive totals and the
                // intrusive LRU list in lockstep.
                cache.CleanCaches();

                heap.Dispose();
                interner.Dispose();
            }
        }

        [Test]
        public void InputDescriptorSweep_NativeHeap_ForgetsSourceAndBytes()
        {
            // Native mirror of the core forget: exercises the native input heap's release-notify
            // hooks and the native-bytes branch of BlobCache.TryEvictInactive (inactive native
            // byte totals, not the managed entry count).
            var registry = CreateRegistry();
            using var pool = new NativeBlobBoxPool();
            using var cache = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, pool);
            var interner = new BlobFactory(TrecsLog.Default, cache, registry);
            interner.AddFactory<Descriptor>(
                new NativeDescriptorBlobFactory<Descriptor, Blob>(d => new Blob(d.Seed * d.Seed))
            );
            var heap = new InputNativeSharedHeap(TrecsLog.Default, cache, interner);

            var ptr = heap.AcquireFromDescriptor<Descriptor, Blob>(frame: 5, new Descriptor(4));
            NAssert.AreEqual(16, cache.GetNativeBlobRef<Blob>(ptr.BlobId).Value);

            heap.ClearAtOrBeforeFrame(5);
            var liveIds = new IterableHashSet<BlobId>();
            heap.AddReferencedBlobIds(liveIds);
            interner.SweepInputDescriptors(liveIds);

            NAssert.IsFalse(interner.IsRegistered(ptr.BlobId));
            NAssert.IsFalse(cache.IsResident(ptr.BlobId));

            // DEBUG VerifyLruConsistency: the targeted native evict kept the inactive byte total
            // and the intrusive LRU list in lockstep.
            cache.CleanCaches();

            // Re-acquire re-derives identical content under the same content-derived id.
            var rePtr = heap.AcquireFromDescriptor<Descriptor, Blob>(frame: 9, new Descriptor(4));
            NAssert.AreEqual(ptr.BlobId, rePtr.BlobId);
            NAssert.AreEqual(16, cache.GetNativeBlobRef<Blob>(rePtr.BlobId).Value);

            heap.Dispose();
            interner.Dispose();
        }

        [Test]
        public void InputDescriptorSweep_LiveFrameReference_Kept()
        {
            // Liveness is computed against the heap's actual live entries at sweep time: a later
            // frame still holding the same descriptor blob keeps it fully alive through the sweep.
            using var pool = new NativeBlobBoxPool();
            var (cache, interner, heap) = CreateInputHarness(pool);
            using (cache)
            {
                var ptr = heap.AcquireFromDescriptor<Descriptor, string>(
                    frame: 5,
                    new Descriptor(3)
                );
                heap.AcquireFromDescriptor<Descriptor, string>(frame: 8, new Descriptor(3));

                heap.ClearAtOrBeforeFrame(5);
                Sweep(interner, heap);

                NAssert.IsTrue(interner.IsRegistered(ptr.BlobId));
                NAssert.IsTrue(cache.IsResident(ptr.BlobId));
                NAssert.AreEqual("seed-3", cache.GetManagedBlob<string>(ptr.BlobId));

                // Once that frame trims too, the next sweep forgets it.
                heap.ClearAtOrBeforeFrame(8);
                Sweep(interner, heap);
                NAssert.IsFalse(interner.IsRegistered(ptr.BlobId));

                heap.Dispose();
                interner.Dispose();
            }
        }

        [Test]
        public void InputDescriptorSweep_ByIdReferenceEntry_Kept()
        {
            // A plain by-id input acquire of a descriptor blob creates a Reference-kind entry in a
            // later frame. The sweep's mark phase walks *all* live entries, so that entry keeps the
            // id alive even after the descriptor-carrying frame trims — otherwise an in-session
            // replay of a stream window containing the Reference would find no source.
            using var pool = new NativeBlobBoxPool();
            var (cache, interner, heap) = CreateInputHarness(pool);
            using (cache)
            {
                var ptr = heap.AcquireFromDescriptor<Descriptor, string>(
                    frame: 5,
                    new Descriptor(3)
                );
                NAssert.IsTrue(heap.TryAcquire<string>(frame: 8, ptr.BlobId, out _));

                heap.ClearAtOrBeforeFrame(5);
                Sweep(interner, heap);

                NAssert.IsTrue(interner.IsRegistered(ptr.BlobId));
                NAssert.IsTrue(cache.IsResident(ptr.BlobId));

                heap.Dispose();
                interner.Dispose();
            }
        }

        [Test]
        public void InputDescriptorSweep_PromotedId_KeptPermanently()
        {
            // Promotion is the keep-signal: once a deterministic context justifies the id (here a
            // sim-side intern of the same descriptor; the input→sim pointer conversion is the other
            // path), it is sim state — the sweep must leave source and journal entry alone even
            // with no live input frame, or the next snapshot could not restore it.
            using var pool = new NativeBlobBoxPool();
            var (cache, interner, heap) = CreateInputHarness(pool);
            using (cache)
            {
                var ptr = heap.AcquireFromDescriptor<Descriptor, string>(
                    frame: 5,
                    new Descriptor(3)
                );
                interner.Intern(new Descriptor(3)); // deterministic intern -> promotes

                heap.ClearAtOrBeforeFrame(5);
                Sweep(interner, heap);

                NAssert.IsTrue(interner.IsRegistered(ptr.BlobId));

                // The journal entry survived too: an active pin of the id still collects it.
                var anchor = interner.AcquireSharedAnchor<string>(ptr.BlobId);
                var activeIds = new IterableHashSet<BlobId>();
                cache.GetAllActiveBlobIds(activeIds);
                var journal = new IterableDictionary<BlobId, object>();
                interner.CollectJournaledDescriptors(activeIds, journal);
                NAssert.IsTrue(journal.ContainsKey(ptr.BlobId));
                NAssert.AreEqual(3, ((Descriptor)journal[ptr.BlobId]).Seed);

                anchor.Dispose(cache);
                heap.Dispose();
                interner.Dispose();
            }
        }

        [Test]
        public void InputDescriptorSweep_ReAcquireAfterSweep_ReDerivesAndRejournals()
        {
            // Successor of the old re-intern-after-prune regression: a swept descriptor is a pure
            // recipe, so acquiring it again re-registers the source, re-derives identical bytes
            // under the same content-derived id, and re-journals — nothing is lost for good.
            using var pool = new NativeBlobBoxPool();
            var (cache, interner, heap) = CreateInputHarness(pool);
            using (cache)
            {
                var ptr = heap.AcquireFromDescriptor<Descriptor, string>(
                    frame: 5,
                    new Descriptor(3)
                );
                heap.ClearAtOrBeforeFrame(5);
                Sweep(interner, heap);
                NAssert.IsFalse(interner.IsRegistered(ptr.BlobId));

                var rePtr = heap.AcquireFromDescriptor<Descriptor, string>(
                    frame: 9,
                    new Descriptor(3)
                );
                NAssert.AreEqual(ptr.BlobId, rePtr.BlobId);
                NAssert.AreEqual("seed-3", cache.GetManagedBlob<string>(rePtr.BlobId));

                // And a sim-side intern after the sweep re-journals for snapshots (the registering
                // branch of Intern — the journal write the old PruneJournal test guarded).
                interner.Intern(new Descriptor(3));
                var anchor = interner.AcquireSharedAnchor<string>(rePtr.BlobId);
                var activeIds = new IterableHashSet<BlobId>();
                cache.GetAllActiveBlobIds(activeIds);
                var journal = new IterableDictionary<BlobId, object>();
                interner.CollectJournaledDescriptors(activeIds, journal);
                NAssert.IsTrue(journal.ContainsKey(rePtr.BlobId));

                anchor.Dispose(cache);
                heap.Dispose();
                interner.Dispose();
            }
        }

        [Test]
        public void InputDescriptorSweep_InSessionReplayAfterSweep_RestoresFromStream()
        {
            // An in-session replay (same world, source already swept) must still re-derive the
            // blob: the input stream carries the descriptor per entry, and Deserialize routes it
            // through RestoreInputDescriptor, which re-registers the swept source.
            var registry = CreateRegistry();
            using var pool = new NativeBlobBoxPool();
            using var cache = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, pool);
            var interner = new BlobFactory(TrecsLog.Default, cache, registry);
            interner.AddFactory<Descriptor>(
                new ManagedDescriptorBlobFactory<Descriptor, string>(d => $"seed-{d.Seed}")
            );
            var heap = new InputSharedHeap(TrecsLog.Default, cache, interner);

            var ptr = heap.AcquireFromDescriptor<Descriptor, string>(frame: 5, new Descriptor(3));

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();
            var readBuffer = new SerializationReadBuffer();
            helper.Writer.Start(data, version: 1, includeTypeChecks: true);
            heap.Serialize(helper.Writer);
            helper.Writer.Complete();
            var bytes = data.ToContiguousBytes();

            heap.ClearAtOrBeforeFrame(5);
            Sweep(interner, heap);
            NAssert.IsFalse(interner.IsRegistered(ptr.BlobId));

            helper.Reader.Start(readBuffer.Wrap(bytes));
            heap.Deserialize(helper.Reader);
            helper.Reader.Complete();

            NAssert.IsTrue(cache.IsResident(ptr.BlobId));
            NAssert.AreEqual("seed-3", cache.GetManagedBlob<string>(ptr.BlobId));

            heap.Dispose();
            interner.Dispose();
        }

        [Test]
        public void InputDescriptorSweep_EndToEnd_MaxClearFrameTrimSweeps()
        {
            // Full wiring: enough distinct input descriptors to trip the churn trigger, then a few
            // fixed-frame steps so the advancing max-clear-frame trim releases the entries (more
            // churn) and runs the sweep — every unpromoted descriptor source is forgotten without
            // any explicit GC call.
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var inputAcc = env.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "SweepEndToEnd"
            );
            SharedAnchor.Register<Descriptor, string>(env.Accessor, d => $"seed-{d.Seed}");

            var ids = new List<BlobId>();
            for (int i = 0; i < BlobFactory.InputDescriptorSweepChurn; i++)
            {
                ids.Add(
                    InputSharedPtr
                        .Acquire<Descriptor, string>(inputAcc, new Descriptor(1000 + i))
                        .BlobId
                );
            }
            foreach (var id in ids)
            {
                NAssert.IsTrue(env.Accessor.BlobFactory.IsRegistered(id));
            }

            // Step past the acquiring frame: max clear frame advances over it, the trim releases
            // the frame entries, and the (now-due) sweep forgets the descriptors.
            env.StepFixedFrames(3);

            foreach (var id in ids)
            {
                NAssert.IsFalse(env.Accessor.BlobFactory.IsRegistered(id));
            }
        }

        [Test]
        public void SnapshotMetadata_RoundTrips()
        {
            // The descriptor journal no longer lives in metadata (it travels inside the world-state
            // stream — see WorldStateSerializer's BlobJournal section, covered by
            // Snapshot_FreshWorldLoad_ReDerivesBlobFromJournal). This just guards the metadata
            // header's own round-trip.
            var registry = CreateRegistry();

            var metadata = new SnapshotMetadata { Version = 1, FixedFrame = 5 };
            metadata.BlobIds.Add(new BlobId(10));

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();
            var readBuffer = new SerializationReadBuffer();
            helper.WriteAll<SnapshotMetadata>(data, metadata, version: 1, includeTypeChecks: true);
            var bytes = data.ToContiguousBytes();

            var restored = helper.ReadAll<SnapshotMetadata>(readBuffer.Wrap(bytes));

            NAssert.AreEqual(1, restored.Version);
            NAssert.AreEqual(5, restored.FixedFrame);
            NAssert.AreEqual(1, restored.BlobIds.Count);
        }

        [Test]
        public void Snapshot_FreshWorldLoad_ReDerivesBlobFromJournal()
        {
            // World A interns + pins a derivable blob, then snapshots.
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            NativeSharedAnchor.Register<Descriptor, Blob>(accA, d => new Blob(d.Seed * d.Seed));

            var id = accA.BlobFactory.Intern(new Descriptor(4));
            var ptrA = NativeSharedPtr.Acquire<Blob>(accA, id);

            var registryA = envA.World.SerializerRegistry;
            var snapshotsA = new SnapshotSerializer(
                envA.World,
                registryA,
                new WorldStateSerializer(envA.World)
            );

            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            // World B has the factory registered but never interned this descriptor — the journal is
            // its only way to recover the source.
            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var accB = envB.Accessor;
            NativeSharedAnchor.Register<Descriptor, Blob>(accB, d => new Blob(d.Seed * d.Seed));

            NAssert.IsFalse(envB.World.GetBlobCache().IsResident(id));

            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            // The journal re-registered the source before the heaps deserialized, so the blob is
            // resident and re-derived to the same value (4 * 4 = 16).
            NAssert.IsTrue(envB.World.GetBlobCache().IsResident(id));
            NAssert.AreEqual(16, envB.World.GetBlobCache().GetNativeBlobRef<Blob>(id).Value);

            ptrA.Dispose(accA);
        }

        [Test]
        public void Snapshot_AnchorOnlyDescriptorBlob_StaysOutOfSnapshot()
        {
            // Snapshots are heap-derived: they carry exactly what the fixed-domain heaps reference.
            // An anchor is an *ambient* hold — invisible to snapshots — so a descriptor blob pinned
            // only through NativeSharedAnchor.Acquire<TDesc,T> must NOT enter the snapshot's journal
            // (otherwise snapshot content/checksums would depend on who happens to hold ambient pins).
            // Its source of truth is the recipe (descriptor + registered builder), not the snapshot:
            // a fresh world re-derives it by re-acquiring, never by loading.
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            NativeSharedAnchor.Register<Descriptor, Blob>(accA, d => new Blob(d.Seed * d.Seed));

            // Pin by descriptor through the anchor: interns + pins, but no heap references it.
            var anchorA = NativeSharedAnchor.Acquire<Descriptor, Blob>(accA, new Descriptor(4));
            var id = anchorA.BlobId;

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            // World B has the builder registered but never interned this descriptor.
            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var accB = envB.Accessor;
            NativeSharedAnchor.Register<Descriptor, Blob>(accB, d => new Blob(d.Seed * d.Seed));

            NAssert.IsFalse(accB.BlobFactory.IsRegistered(id));

            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            // The anchor-only blob did not ride the snapshot: world B still has no source for the id.
            NAssert.IsFalse(accB.BlobFactory.IsRegistered(id));

            // But the blob was never lost — the recipe is its source of truth, so re-acquiring the
            // same descriptor re-interns and re-derives the identical blob (4 * 4 = 16).
            var anchorB = NativeSharedAnchor.Acquire<Descriptor, Blob>(accB, new Descriptor(4));
            NAssert.AreEqual(id, anchorB.BlobId);
            NAssert.AreEqual(16, anchorB.Get(accB).Value);

            anchorA.Dispose(accA);
            anchorB.Dispose(accB);
        }

        [Test]
        public void Snapshot_HeapReferencedAndAnchorOnlyBlobs_OnlyHeapReferencedJournaled()
        {
            // The discriminating case for heap-derived snapshots: two derivable blobs alive in the
            // same world, one referenced by a NativeSharedPtr (fixed/sim domain), one held only by an
            // ambient anchor. The snapshot journal must carry exactly the heap-referenced one.
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            NativeSharedAnchor.Register<Descriptor, Blob>(accA, d => new Blob(d.Seed * d.Seed));

            var simId = accA.BlobFactory.Intern(new Descriptor(5));
            var simPtr = NativeSharedPtr.Acquire<Blob>(accA, simId);
            var ambientAnchor = NativeSharedAnchor.Acquire<Descriptor, Blob>(
                accA,
                new Descriptor(6)
            );
            var ambientId = ambientAnchor.BlobId;

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var accB = envB.Accessor;
            NativeSharedAnchor.Register<Descriptor, Blob>(accB, d => new Blob(d.Seed * d.Seed));

            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            // The sim-held blob rode the journal and re-derived (5 * 5 = 25)...
            NAssert.IsTrue(accB.BlobFactory.IsRegistered(simId));
            NAssert.AreEqual(25, envB.World.GetBlobCache().GetNativeBlobRef<Blob>(simId).Value);

            // ...the anchor-only one did not.
            NAssert.IsFalse(accB.BlobFactory.IsRegistered(ambientId));

            simPtr.Dispose(accA);
            ambientAnchor.Dispose(accA);
        }

        [Test]
        public void Snapshot_InputHeapHeldBlobs_StayOutOfWorldSnapshot()
        {
            // Input blobs belong to the recording's input stream, not the world snapshot: snapshots
            // are heap-derived (fixed/sim heaps only), so blobs held only by the input heaps must not
            // enter the snapshot's journal or its opaque-blob set. Two flavors:
            //
            // 1. An eager (sourceless) input alloc: if it leaked into the snapshot's blob set, saving
            //    without an opaque store would throw the requireOpaqueStore guard. It must not.
            // 2. A descriptor-interned input blob: its descriptor rides the input stream (see the
            //    InputSharedHeap_DescriptorAcquire_* tests), not the snapshot journal — a fresh world
            //    loading the snapshot must NOT recover the source from it.
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            var inputAcc = envA.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "InputBlobSnapshotTest"
            );
            SharedAnchor.Register<Descriptor, string>(envA.Accessor, d => $"seed-{d.Seed}");

            var eagerPtr = InputSharedPtr.Alloc(inputAcc, "input-only-opaque-payload");
            var derivedPtr = InputSharedPtr.Acquire<Descriptor, string>(
                inputAcc,
                new Descriptor(8)
            );
            NAssert.IsFalse(eagerPtr.IsNull);

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();

            // (1) No opaque store supplied: must succeed, because the eager input blob is not part
            // of the world snapshot. (Were input-heap pins still counted, this would throw the
            // save-time requireOpaqueStore guard — see Snapshot_SaveOpaqueBlob_WithoutStore_Throws.)
            NAssert.DoesNotThrow(() => snapshotsA.SaveSnapshotToStream(version: 1, stream: stream));

            // (2) A fresh world loads the snapshot: neither input blob came along.
            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Descriptor>()),
                TestTemplates.SimpleAlpha
            );
            SharedAnchor.Register<Descriptor, string>(envB.Accessor, d => $"seed-{d.Seed}");

            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            NAssert.IsFalse(envB.World.GetBlobCache().IsResident(eagerPtr.BlobId));
            NAssert.IsFalse(envB.World.GetBlobCache().IsResident(derivedPtr.BlobId));
            NAssert.IsFalse(envB.Accessor.BlobFactory.IsRegistered(derivedPtr.BlobId));
        }

        [Test]
        public void InputSharedHeap_DescriptorAcquire_ReDerivesInFreshCacheFromRecording()
        {
            // The input-side analog of Snapshot_FreshWorldLoad: an input blob acquired from a
            // descriptor records that descriptor into the input stream, so a fresh process replaying
            // the recording re-registers the source and re-derives the bytes — without the
            // descriptor ever entering a snapshot.
            var registry = CreateRegistry();

            using var poolA = new NativeBlobBoxPool();
            using var cacheA = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolA);
            var internerA = new BlobFactory(TrecsLog.Default, cacheA, registry);
            internerA.AddFactory<Descriptor>(
                new ManagedDescriptorBlobFactory<Descriptor, string>(d => $"seed-{d.Seed}")
            );
            var heapA = new InputSharedHeap(TrecsLog.Default, cacheA, internerA);

            var ptr = heapA.AcquireFromDescriptor<Descriptor, string>(frame: 5, new Descriptor(3));
            NAssert.AreEqual("seed-3", cacheA.GetManagedBlob<string>(ptr.BlobId));

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();
            var readBuffer = new SerializationReadBuffer();
            helper.Writer.Start(data, version: 1, includeTypeChecks: true);
            heapA.Serialize(helper.Writer);
            helper.Writer.Complete();
            var bytes = data.ToContiguousBytes();

            // Fresh world: the factory is registered, but this descriptor was never interned here,
            // so the descriptor recorded in the input stream is the only way to recover the blob.
            using var poolB = new NativeBlobBoxPool();
            using var cacheB = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolB);
            var internerB = new BlobFactory(TrecsLog.Default, cacheB, registry);
            internerB.AddFactory<Descriptor>(
                new ManagedDescriptorBlobFactory<Descriptor, string>(d => $"seed-{d.Seed}")
            );
            var heapB = new InputSharedHeap(TrecsLog.Default, cacheB, internerB);

            NAssert.IsFalse(cacheB.IsResident(ptr.BlobId));

            helper.Reader.Start(readBuffer.Wrap(bytes));
            heapB.Deserialize(helper.Reader);
            helper.Reader.Complete();

            NAssert.IsTrue(cacheB.IsResident(ptr.BlobId));
            NAssert.AreEqual("seed-3", cacheB.GetManagedBlob<string>(ptr.BlobId));

            heapA.Dispose();
            heapB.Dispose();
            internerA.Dispose();
            internerB.Dispose();
        }

        [Test]
        public void InputNativeSharedHeap_DescriptorAcquire_ReDerivesInFreshCacheFromRecording()
        {
            // Native counterpart to InputSharedHeap_DescriptorAcquire_*: same recorded-descriptor
            // re-derivation, through the native input heap + its burst resolver.
            var registry = CreateRegistry();

            using var poolA = new NativeBlobBoxPool();
            using var cacheA = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolA);
            var internerA = new BlobFactory(TrecsLog.Default, cacheA, registry);
            internerA.AddFactory<Descriptor>(
                new NativeDescriptorBlobFactory<Descriptor, Blob>(d => new Blob(d.Seed * d.Seed))
            );
            var heapA = new InputNativeSharedHeap(TrecsLog.Default, cacheA, internerA);

            var ptr = heapA.AcquireFromDescriptor<Descriptor, Blob>(frame: 5, new Descriptor(4));
            NAssert.AreEqual(16, cacheA.GetNativeBlobRef<Blob>(ptr.BlobId).Value);

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();
            var readBuffer = new SerializationReadBuffer();
            helper.Writer.Start(data, version: 1, includeTypeChecks: true);
            heapA.Serialize(helper.Writer);
            helper.Writer.Complete();
            var bytes = data.ToContiguousBytes();

            using var poolB = new NativeBlobBoxPool();
            using var cacheB = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolB);
            var internerB = new BlobFactory(TrecsLog.Default, cacheB, registry);
            internerB.AddFactory<Descriptor>(
                new NativeDescriptorBlobFactory<Descriptor, Blob>(d => new Blob(d.Seed * d.Seed))
            );
            var heapB = new InputNativeSharedHeap(TrecsLog.Default, cacheB, internerB);

            NAssert.IsFalse(cacheB.IsResident(ptr.BlobId));

            helper.Reader.Start(readBuffer.Wrap(bytes));
            heapB.Deserialize(helper.Reader);
            helper.Reader.Complete();

            NAssert.IsTrue(cacheB.IsResident(ptr.BlobId));
            NAssert.AreEqual(16, cacheB.GetNativeBlobRef<Blob>(ptr.BlobId).Value);

            heapA.Dispose();
            heapB.Dispose();
            internerA.Dispose();
            internerB.Dispose();
        }

        [Test]
        public void AllocNativeBlob_ContentAddressed_DedupsEqualContent()
        {
            using var pool = new NativeBlobBoxPool();
            using var cache = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, pool);

            // No caller-supplied id: the id is derived from the blob's content bytes.
            var p1 = cache.AllocNativeBlob<Blob>(new Blob(42));
            var p2 = cache.AllocNativeBlob<Blob>(new Blob(42));
            var p3 = cache.AllocNativeBlob<Blob>(new Blob(43));

            // Equal content collapses to one id (and one cache entry); different content differs.
            NAssert.AreEqual(p1.BlobId, p2.BlobId);
            NAssert.AreNotEqual(p1.BlobId, p3.BlobId);
            NAssert.AreEqual(42, cache.GetNativeBlobRef<Blob>(p1.BlobId).Value);
            NAssert.AreEqual(43, cache.GetNativeBlobRef<Blob>(p3.BlobId).Value);

            p1.Dispose(cache);
            p2.Dispose(cache);
            p3.Dispose(cache);
        }

        // In-memory IOpaqueBlobStore for tests: the byte-store backend the snapshot path persists
        // opaque (eager) blob bytes to / restores them from. svkj's FileBlobStore is the on-disk one.
        sealed class InMemoryOpaqueBlobStore : IOpaqueBlobStore
        {
            readonly Dictionary<BlobId, byte[]> _map = new();

            public int Count => _map.Count;

            public bool Contains(BlobId id) => _map.ContainsKey(id);

            public void Write(BlobId id, Action<Stream> writeContents)
            {
                using var stream = new MemoryStream();
                writeContents(stream);
                _map[id] = stream.ToArray();
            }

            public bool TryOpenRead(BlobId id, out Stream stream)
            {
                if (_map.TryGetValue(id, out var bytes))
                {
                    stream = new MemoryStream(bytes, writable: false);
                    return true;
                }
                stream = null;
                return false;
            }
        }

        [Test]
        public void Snapshot_FreshWorldLoad_RestoresOpaqueBlobFromStore()
        {
            // An *opaque* (eager) blob has no descriptor/factory to re-derive it, so the journal can't
            // recover it — its bytes must be persisted to the IOpaqueBlobStore and restored before the
            // heaps re-pin it by id. World A allocs it straight into the native shared heap (the
            // sim-side content-addressed Alloc, which inserts + pins atomically); a fresh World B
            // recovers it from the store alone.
            var store = new InMemoryOpaqueBlobStore();

            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            var heapPtrA = NativeSharedPtr.Alloc<Blob>(accA, new Blob(7));
            var id = heapPtrA.GetBlobId(accA);

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(
                version: 1,
                stream: stream,
                opaqueBlobStore: store,
                world: envA.World
            );

            // The opaque blob's bytes were persisted out-of-band to the store.
            NAssert.IsTrue(store.Contains(id));

            // Fresh world that has never seen this blob; only the store holds its bytes.
            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            NAssert.IsFalse(envB.World.GetBlobCache().IsResident(id));

            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream, opaqueBlobStore: store, world: envB.World);

            // Restored from the store before the heaps pinned it, and reads back the same value.
            NAssert.IsTrue(envB.World.GetBlobCache().IsResident(id));
            NAssert.AreEqual(7, envB.World.GetBlobCache().GetNativeBlobRef<Blob>(id).Value);

            heapPtrA.Dispose(accA);
        }

        [Test]
        public void Snapshot_LoadOpaqueBlob_WithoutStore_Throws()
        {
            // Loading a snapshot that references opaque blobs whose bytes are not resident (and
            // were not restored first via PeekOpaqueBlobRefs + OpaqueBlobs.Restore) should fail
            // loudly (rather than the cryptic "no source to materialize" at the heap CreateHandle).
            var store = new InMemoryOpaqueBlobStore();

            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            var heapPtrA = NativeSharedPtr.Alloc<Blob>(accA, new Blob(9));

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(
                version: 1,
                stream: stream,
                opaqueBlobStore: store,
                world: envA.World
            );

            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;

            // No restore pre-step → the referenced opaque blob is not resident.
            NAssert.Catch(() => snapshotsB.LoadSnapshot(stream));

            heapPtrA.Dispose(accA);
        }

        [Test]
        public void InputNativeSharedHeap_OpaqueAlloc_RestoresInFreshCacheFromStore()
        {
            // An opaque (eager) *input* blob — Alloc'd with no descriptor — has no source to
            // re-derive from, so the recording stream persists its bytes to the IOpaqueBlobStore and
            // a fresh-process replay restores them before re-minting the frame handle.
            var registry = CreateRegistry();
            var store = new InMemoryOpaqueBlobStore();
            var opaqueBaker = new OpaqueBlobBaker(registry);

            using var poolA = new NativeBlobBoxPool();
            using var cacheA = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolA);
            var internerA = new BlobFactory(TrecsLog.Default, cacheA, registry);
            var heapA = new InputNativeSharedHeap(TrecsLog.Default, cacheA, internerA);

            var id = new BlobId(515151);
            heapA.Alloc<Blob>(frame: 7, id, new Blob(99));
            NAssert.AreEqual(99, cacheA.GetNativeBlobRef<Blob>(id).Value);

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();
            var readBuffer = new SerializationReadBuffer();
            helper.Writer.Start(data, version: 1, includeTypeChecks: true);
            heapA.Serialize(helper.Writer, store, opaqueBaker);
            helper.Writer.Complete();
            var bytes = data.ToContiguousBytes();

            NAssert.IsTrue(store.Contains(id));

            using var poolB = new NativeBlobBoxPool();
            using var cacheB = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolB);
            var internerB = new BlobFactory(TrecsLog.Default, cacheB, registry);
            var heapB = new InputNativeSharedHeap(TrecsLog.Default, cacheB, internerB);
            NAssert.IsFalse(cacheB.IsResident(id));

            helper.Reader.Start(readBuffer.Wrap(bytes));
            heapB.Deserialize(helper.Reader, store, opaqueBaker);
            helper.Reader.Complete();

            NAssert.IsTrue(cacheB.IsResident(id));
            NAssert.AreEqual(99, cacheB.GetNativeBlobRef<Blob>(id).Value);

            heapA.Dispose();
            heapB.Dispose();
            internerA.Dispose();
            internerB.Dispose();
        }

        [Test]
        public void InputSharedHeap_OpaqueAlloc_RestoresInFreshCacheFromStore()
        {
            // Managed counterpart: an opaque managed input blob round-trips its bytes through the
            // store (serialized via the binary serializer rather than the native NBLB format).
            var registry = CreateRegistry();
            var store = new InMemoryOpaqueBlobStore();
            var opaqueBaker = new OpaqueBlobBaker(registry);

            using var poolA = new NativeBlobBoxPool();
            using var cacheA = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolA);
            var internerA = new BlobFactory(TrecsLog.Default, cacheA, registry);
            var heapA = new InputSharedHeap(TrecsLog.Default, cacheA, internerA);

            // Content-addressed: the id is derived from the value rather than chosen up front.
            var ptr = heapA.Alloc<string>(frame: 3, "opaque-input");
            var id = ptr.BlobId;
            NAssert.AreEqual("opaque-input", cacheA.GetManagedBlob<string>(id));

            var helper = new SerializationHelper(registry);
            var data = new SerializationData();
            var readBuffer = new SerializationReadBuffer();
            helper.Writer.Start(data, version: 1, includeTypeChecks: true);
            heapA.Serialize(helper.Writer, store, opaqueBaker);
            helper.Writer.Complete();
            var bytes = data.ToContiguousBytes();

            NAssert.IsTrue(store.Contains(id));

            using var poolB = new NativeBlobBoxPool();
            using var cacheB = new BlobCache(TrecsLog.Default, BlobCacheSettings.Default, poolB);
            var internerB = new BlobFactory(TrecsLog.Default, cacheB, registry);
            var heapB = new InputSharedHeap(TrecsLog.Default, cacheB, internerB);
            NAssert.IsFalse(cacheB.IsResident(id));

            helper.Reader.Start(readBuffer.Wrap(bytes));
            heapB.Deserialize(helper.Reader, store, opaqueBaker);
            helper.Reader.Complete();

            NAssert.IsTrue(cacheB.IsResident(id));
            NAssert.AreEqual("opaque-input", cacheB.GetManagedBlob<string>(id));

            heapA.Dispose();
            heapB.Dispose();
            internerA.Dispose();
            internerB.Dispose();
        }

        [Test]
        public void Snapshot_FreshWorldLoad_RestoresOpaqueBlob_ViaOnDiskFileBlobStore()
        {
            // The on-disk FileBlobStore is what the editor .snap / .trec paths use. Round-trip an
            // opaque (eager) blob through a real directory: World A allocs + pins it and saves with
            // the store; a fresh World B (and a freshly-reopened store, as a new process would have)
            // recovers it from the files alone. Also checks store-level content-addressed dedup —
            // saving the same blob a second time writes no new file.
            var dir = Path.Combine(
                Path.GetTempPath(),
                "trecs_blobstore_" + Path.GetRandomFileName()
            );
            try
            {
                var store = new FileBlobStore(dir);

                using var envA = EcsTestHelper.CreateEnvironment(
                    b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                    TestTemplates.SimpleAlpha
                );
                var accA = envA.Accessor;
                var heapPtrA = NativeSharedPtr.Alloc<Blob>(accA, new Blob(42));
                var id = heapPtrA.GetBlobId(accA);

                var snapshotsA = new SnapshotSerializer(
                    envA.World,
                    envA.World.SerializerRegistry,
                    new WorldStateSerializer(envA.World)
                );
                using var stream = new MemoryStream();
                snapshotsA.SaveSnapshotToStream(
                    version: 1,
                    stream: stream,
                    opaqueBlobStore: store,
                    world: envA.World
                );
                NAssert.IsTrue(store.Contains(id));

                // Content-addressed: saving the same resident blob again is a no-op on disk.
                using (var stream2 = new MemoryStream())
                {
                    snapshotsA.SaveSnapshotToStream(
                        version: 1,
                        stream: stream2,
                        opaqueBlobStore: store,
                        world: envA.World
                    );
                }
                int storedCount = 0;
                foreach (var _ in store.EnumerateIds())
                {
                    storedCount++;
                }
                NAssert.AreEqual(1, storedCount);

                // A fresh process would construct a new FileBlobStore over the same directory.
                var reopened = new FileBlobStore(dir);
                using var envB = EcsTestHelper.CreateEnvironment(
                    b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                    TestTemplates.SimpleAlpha
                );
                NAssert.IsFalse(envB.World.GetBlobCache().IsResident(id));

                var snapshotsB = new SnapshotSerializer(
                    envB.World,
                    envB.World.SerializerRegistry,
                    new WorldStateSerializer(envB.World)
                );
                stream.Position = 0;
                snapshotsB.LoadSnapshot(stream, opaqueBlobStore: reopened, world: envB.World);

                NAssert.IsTrue(envB.World.GetBlobCache().IsResident(id));
                NAssert.AreEqual(42, envB.World.GetBlobCache().GetNativeBlobRef<Blob>(id).Value);

                heapPtrA.Dispose(accA);
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }

        [Test]
        public void Snapshot_RetainedData_RestoresOpaqueBlob_FromStore()
        {
            // The editor recorder captures keyframes/bookmarks into a retained SerializationData (not
            // a stream) and reloads them via the IReadOnlySerializationData overload. Exercise that
            // path with an opaque blob + store, the way TrecsRewindBuffer does.
            var store = new InMemoryOpaqueBlobStore();

            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            var heapPtrA = NativeSharedPtr.Alloc<Blob>(accA, new Blob(11));
            var id = heapPtrA.GetBlobId(accA);

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            var data = new SerializationData();
            var opaqueBlobIds = new List<BlobId>();
            snapshotsA.SaveSnapshot(version: 1, data, includeTypeChecks: true, opaqueBlobIds);
            var opaqueBlobsA = envA.World.CreateOpaqueBlobs();
            foreach (var blobId in opaqueBlobIds)
            {
                opaqueBlobsA.Persist(blobId, store);
            }
            NAssert.IsTrue(store.Contains(id));

            using var envB = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            snapshotsB.LoadSnapshot(data, opaqueBlobStore: store, world: envB.World);

            NAssert.IsTrue(envB.World.GetBlobCache().IsResident(id));
            NAssert.AreEqual(11, envB.World.GetBlobCache().GetNativeBlobRef<Blob>(id).Value);

            heapPtrA.Dispose(accA);
        }

        [Test]
        public void Snapshot_SaveOpaqueBlob_WithoutStore_Throws()
        {
            // A durable save that references opaque (eager) blobs but is given no store would emit
            // dangling refs whose bytes go nowhere — an unloadable snapshot. The save must fail
            // loudly at save time (the requireOpaqueStore guard), not silently.
            using var envA = EcsTestHelper.CreateEnvironment(
                b => b.RegisterSerializer(new BlitSerializer<Blob>()),
                TestTemplates.SimpleAlpha
            );
            var accA = envA.Accessor;
            var heapPtrA = NativeSharedPtr.Alloc<Blob>(accA, new Blob(5));

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();

            NAssert.Catch(() => snapshotsA.SaveSnapshotToStream(version: 1, stream: stream));

            heapPtrA.Dispose(accA);
        }
    }
}
