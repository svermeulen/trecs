using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests for the blob determinism-domain rules
    /// (docs/maintainers/maintainer-docs/blob-determinism-domains.md):
    /// the deterministic (simulation) by-id resolve consults only deterministic registries — held
    /// by a sim heap, or backed by a deterministically registered source — never raw cache
    /// residency; ambient-context interns are invisible to it until promoted; anchors are
    /// role-gated out of fixed simulation; and the input→sim pointer conversion is the blessed
    /// bridge for input payloads.
    /// </summary>
    [TestFixture]
    public class BlobDeterminismDomainTests
    {
        readonly struct SeedDescriptor
        {
            public readonly int Seed;

            public SeedDescriptor(int seed)
            {
                Seed = seed;
            }
        }

        static TestEnvironment CreateEnv(BlobCacheSettings blobCacheSettings = null)
        {
            return EcsTestHelper.CreateEnvironment(
                builder =>
                {
                    builder.RegisterSerializer(new BlitSerializer<SeedDescriptor>());
                    if (blobCacheSettings != null)
                    {
                        builder.SetBlobCacheSettings(blobCacheSettings);
                    }
                },
                TestTemplates.SimpleAlpha
            );
        }

        // (a) A blob kept alive only by an ambient anchor is invisible to the sim resolve: residency
        // is not a deterministic justification, so TryAcquire answers a stable false — resident or
        // not — instead of varying with who happens to hold cache pins.
        [Test]
        public void SimTryAcquire_AnchorHeldSourcelessBlob_StableFalse()
        {
            using var env = CreateEnv();
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "SimResolve");

            // Ambient holder (setup/editor shape): content-addressed eager alloc, anchor pin only.
            var anchor = SharedAnchor.Alloc(env.Accessor, "ambient-only-payload");
            var id = anchor.BlobId;
            NAssert.IsTrue(env.World.GetBlobCache().IsResident(id), "Anchor pins the bytes");

            // Resident, but sourceless and not sim-held: the deterministic resolve must say false.
            NAssert.IsFalse(SharedPtr.TryAcquire<string>(fixedAcc, id, out _));

            // The non-Try variant fails loudly with the teaching message.
            NAssert.Throws<TrecsException>(() => SharedPtr.Acquire<string>(fixedAcc, id));

            // Same answer after the ambient hold (and eventually the bytes) go away — false is
            // stable across residency changes.
            anchor.Dispose(env.Accessor);
            NAssert.IsFalse(SharedPtr.TryAcquire<string>(fixedAcc, id, out _));
        }

        // (c) Content-addressed sim Alloc pins atomically (the value in hand is the justification),
        // and once the sim drops its last ref the id is no longer deterministically reachable —
        // even while the bytes still linger resident in the cache's inactive set.
        [Test]
        public void SimAlloc_DropLastRef_TryAcquireFalseRegardlessOfResidency()
        {
            using var env = CreateEnv();
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "SimAlloc");

            var ptr = SharedPtr.Alloc(fixedAcc, "sim-owned-payload");
            var id = ptr.Id;
            NAssert.AreEqual("sim-owned-payload", ptr.Get(fixedAcc));

            // While held: deterministically reachable (heap-held).
            NAssert.IsTrue(SharedPtr.TryAcquire<string>(fixedAcc, id, out var again));
            again.Dispose(fixedAcc);

            ptr.Dispose(fixedAcc);

            // The bytes typically remain resident (inactive LRU) — irrelevant: sourceless and
            // unheld means not deterministically reachable.
            NAssert.IsFalse(SharedPtr.TryAcquire<string>(fixedAcc, id, out _));
        }

        // (d) The flip side: a deterministically registered source survives eviction, so a sim by-id
        // acquire succeeds in every run — the registry, not residency, is what the resolve trusts.
        [Test]
        public void SimAcquire_RegisteredSource_SucceedsAcrossEviction()
        {
            int builds = 0;
            using var env = CreateEnv(
                new BlobCacheSettings
                {
                    MaxInactiveNativeBlobsMb = 0f,
                    MaxInactiveManagedBlobsCount = 0,
                    HighWaterMarkMultiplier = 1f,
                }
            );
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "SimRegistered");

            var id = new BlobId(42);
            // The builder must return identical content every run (the purity contract, enforced
            // by the debug content attestation); the counter proves the rebuild without
            // entering the content.
            SharedAnchor.Register(
                env.Accessor,
                id,
                () =>
                {
                    builds++;
                    return "built";
                }
            );

            NAssert.IsTrue(SharedPtr.TryAcquire<string>(fixedAcc, id, out var ptr1));
            NAssert.AreEqual("built", ptr1.Get(fixedAcc));
            NAssert.AreEqual(1, builds);
            ptr1.Dispose(fixedAcc); // zero inactive cap → bytes evicted immediately

            // Still deterministically reachable: the source re-materializes on demand.
            NAssert.IsTrue(SharedPtr.TryAcquire<string>(fixedAcc, id, out var ptr2));
            NAssert.AreEqual("built", ptr2.Get(fixedAcc));
            NAssert.AreEqual(2, builds, "Eviction must have re-run the builder");
            ptr2.Dispose(fixedAcc);
        }

        // (e) + (f) An ambient-context (Variable-role) descriptor intern registers a source the sim
        // resolve cannot see — by-id resolve fails identically in every run, resident or not — until
        // a deterministic context interns the same descriptor, which promotes it. After promotion
        // the sim's reference snapshot-round-trips into a fresh world via the journal.
        [Test]
        public void AmbientInternedDescriptor_InvisibleToSimResolve_UntilPromoted()
        {
            using var envA = CreateEnv();
            SharedAnchor.Register<SeedDescriptor, string>(envA.Accessor, d => $"seed-{d.Seed}");
            var variableAcc = envA.World.CreateAccessor(AccessorRole.Variable, "AmbientIntern");
            var fixedAcc = envA.World.CreateAccessor(AccessorRole.Fixed, "SimSide");

            // Ambient intern: rendering-style code pins a derivable blob from a Variable context.
            var anchor = SharedAnchor.Acquire<SeedDescriptor, string>(
                variableAcc,
                new SeedDescriptor(3)
            );
            var id = anchor.BlobId;
            NAssert.AreEqual("seed-3", anchor.Get(variableAcc));

            // Resident and source-registered — but ambient-tagged, so the sim resolve must not see
            // it: whether rendering happened to intern this descriptor cannot influence simulation.
            NAssert.IsFalse(SharedPtr.TryAcquire<string>(fixedAcc, id, out _));
            NAssert.Throws<TrecsException>(() => SharedPtr.Acquire<string>(fixedAcc, id));

            // The ambient view is unaffected: another anchor acquire still works.
            NAssert.IsTrue(SharedAnchor.TryAcquire<string>(variableAcc, id, out var anchor2));
            anchor2.Dispose(variableAcc);

            // Promotion: the sim interns the same descriptor itself — a reproducible timeline
            // event — which upgrades the source and journals it.
            var simPtr = SharedPtr.Acquire<SeedDescriptor, string>(fixedAcc, new SeedDescriptor(3));
            NAssert.AreEqual(id, simPtr.Id, "Same descriptor must dedup to the same id");
            NAssert.IsTrue(SharedPtr.TryAcquire<string>(fixedAcc, id, out var byId));
            byId.Dispose(fixedAcc);

            // (f) Snapshot → fresh world: the promoted descriptor rides the journal, so the sim's
            // reference re-derives without the ambient holder existing there at all.
            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            using var envB = CreateEnv();
            SharedAnchor.Register<SeedDescriptor, string>(envB.Accessor, d => $"seed-{d.Seed}");
            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            NAssert.IsTrue(envB.World.GetBlobCache().IsResident(id));
            NAssert.AreEqual("seed-3", envB.World.GetBlobCache().GetManagedBlob<string>(id));

            simPtr.Dispose(fixedAcc);
            anchor.Dispose(variableAcc);
        }

        // (b) The input→sim pointer conversion: an eager payload delivered through the current
        // frame's input pointer converts into a sim-owned SharedPtr, while a bare by-id resolve of
        // the same input-heap-retained blob fails (input-heap retention is history-locker-dependent
        // and therefore not a deterministic predicate — only the in-hand payload is).
        [Test]
        public void InputToSimConversion_EagerPayload_ConvertsWhileByIdFails()
        {
            using var env = CreateEnv();
            var inputAcc = env.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "InputSide"
            );
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "SimSide");

            var inputPtr = InputSharedPtr.Alloc(inputAcc, "frame-payload");

            // Bare by-id resolve of the input-held blob: not deterministically reachable.
            NAssert.IsFalse(SharedPtr.TryAcquire<string>(fixedAcc, inputPtr.BlobId, out _));

            // The blessed bridge: convert the in-hand payload.
            var simPtr = SharedPtr.Acquire(fixedAcc, inputPtr);
            NAssert.AreEqual("frame-payload", simPtr.Get(fixedAcc));

            // Now heap-held, so by-id resolve succeeds (and survives input-frame trims).
            NAssert.IsTrue(SharedPtr.TryAcquire<string>(fixedAcc, inputPtr.BlobId, out var byId));
            byId.Dispose(fixedAcc);
            simPtr.Dispose(fixedAcc);
        }

        // (b, descriptor flavor) Converting a descriptor-acquired input pointer promotes the
        // descriptor into the snapshot journal, so the sim-held reference survives a fresh-world
        // load — without it, the snapshot would carry a bare id no journal entry or opaque section
        // could restore.
        [Test]
        public void InputToSimConversion_DescriptorPayload_JournalsForSnapshot()
        {
            using var envA = CreateEnv();
            SharedAnchor.Register<SeedDescriptor, string>(envA.Accessor, d => $"seed-{d.Seed}");
            var inputAcc = envA.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "InputSide"
            );
            var fixedAcc = envA.World.CreateAccessor(AccessorRole.Fixed, "SimSide");

            var inputPtr = InputSharedPtr.Acquire<SeedDescriptor, string>(
                inputAcc,
                new SeedDescriptor(9)
            );

            // The input-side intern is ambient: invisible to the sim by-id resolve.
            NAssert.IsFalse(SharedPtr.TryAcquire<string>(fixedAcc, inputPtr.BlobId, out _));

            // Conversion pins it into the sim heap and promotes the descriptor into the journal.
            var simPtr = SharedPtr.Acquire(fixedAcc, inputPtr);
            NAssert.AreEqual("seed-9", simPtr.Get(fixedAcc));

            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            // No opaque store needed: the converted blob is derivable, so it travels as a journal
            // entry, not bytes.
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            using var envB = CreateEnv();
            SharedAnchor.Register<SeedDescriptor, string>(envB.Accessor, d => $"seed-{d.Seed}");
            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            NAssert.IsTrue(envB.World.GetBlobCache().IsResident(inputPtr.BlobId));
            NAssert.AreEqual(
                "seed-9",
                envB.World.GetBlobCache().GetManagedBlob<string>(inputPtr.BlobId)
            );

            simPtr.Dispose(fixedAcc);
        }

        // (b, by-id reference flavor) The residual hole in the conversion path: a descriptor-acquired
        // input blob whose descriptor-carrying frame entry has been TRIMMED from the input heap, then
        // re-referenced by bare id (a Reference-kind input entry carries only the id) and converted to
        // sim. The conversion has no descriptor in hand to promote — so unless the factory retained
        // the recipe from the original intern, the snapshot carries a bare id that no journal entry
        // (the input intern skipped the journal) or opaque section (the blob is source-backed, not
        // eager) can restore, and a fresh-world load fails. The conversion must round-trip here just
        // like the in-hand descriptor flavor does.
        [Test]
        public void InputToSimConversion_ByIdRefAfterDescriptorFrameTrimmed_RoundTripsThroughSnapshot()
        {
            using var envA = CreateEnv();
            SharedAnchor.Register<SeedDescriptor, string>(envA.Accessor, d => $"seed-{d.Seed}");
            var fixedAcc = envA.World.CreateAccessor(AccessorRole.Fixed, "SimSide");
            var inputHeap = envA.Accessor.InputSharedHeap;

            // Frame 100: input system acquires from the descriptor. The recipe lives on this frame's
            // input entry (and nowhere else — input interns skip the snapshot journal by design).
            var first = inputHeap.AcquireFromDescriptor<SeedDescriptor, string>(
                frame: 100,
                new SeedDescriptor(5)
            );
            var id = first.BlobId;

            // The input window trims the descriptor-carrying frame.
            inputHeap.ClearAtOrBeforeFrame(100);

            // Frame 500: input code references the same blob by bare id — a Reference-kind entry
            // that records only the id, both in memory and in the recording's input stream.
            var later = inputHeap.Acquire<string>(frame: 500, id);

            // The sim converts the in-hand pointer. Pinning succeeds (the payload is resident)...
            var simPtr = SharedPtr.Acquire(fixedAcc, later);
            NAssert.AreEqual("seed-5", simPtr.Get(fixedAcc));

            // ...and the snapshot must still carry enough to restore the sim's reference.
            var snapshotsA = new SnapshotSerializer(
                envA.World,
                envA.World.SerializerRegistry,
                new WorldStateSerializer(envA.World)
            );
            using var stream = new MemoryStream();
            snapshotsA.SaveSnapshotToStream(version: 1, stream: stream);

            using var envB = CreateEnv();
            SharedAnchor.Register<SeedDescriptor, string>(envB.Accessor, d => $"seed-{d.Seed}");
            var snapshotsB = new SnapshotSerializer(
                envB.World,
                envB.World.SerializerRegistry,
                new WorldStateSerializer(envB.World)
            );
            stream.Position = 0;
            snapshotsB.LoadSnapshot(stream);

            NAssert.IsTrue(envB.World.GetBlobCache().IsResident(id));
            NAssert.AreEqual("seed-5", envB.World.GetBlobCache().GetManagedBlob<string>(id));

            simPtr.Dispose(fixedAcc);
        }

        // Content attestation (debug builds): a builder must be a pure function of its inputs —
        // eviction re-runs it, and for derivable blobs the id hashes the *descriptor*, so divergent
        // rebuilt content is invisible to snapshot checksums. The cache records a content hash per
        // id and asserts identical bytes on every re-insert, turning an impure builder into a loud
        // failure at the exact insert that diverged.
        [Test]
        public void ImpureManagedBuilder_DivergentRematerialization_AssertsAtInsert()
        {
            int builds = 0;
            using var env = CreateEnv(
                new BlobCacheSettings
                {
                    MaxInactiveNativeBlobsMb = 0f,
                    MaxInactiveManagedBlobsCount = 0,
                    HighWaterMarkMultiplier = 1f,
                }
            );
            // Impure on purpose: content depends on how many times the builder has run.
            SharedAnchor.Register<SeedDescriptor, string>(
                env.Accessor,
                d => $"seed-{d.Seed}-build-{++builds}"
            );
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "SimSide");

            var ptr = SharedPtr.Acquire<SeedDescriptor, string>(fixedAcc, new SeedDescriptor(3));
            var id = ptr.Id;
            ptr.Dispose(fixedAcc); // zero inactive cap → bytes evicted immediately

            // Re-materialization produces different bytes under the same id → attestation fires.
            NAssert.Throws<TrecsException>(() => SharedPtr.Acquire<string>(fixedAcc, id));
        }

        [Test]
        public void ImpureNativeBuilder_DivergentRematerialization_AssertsAtInsert()
        {
            int builds = 0;
            using var env = CreateEnv(
                new BlobCacheSettings
                {
                    MaxInactiveNativeBlobsMb = 0f,
                    MaxInactiveManagedBlobsCount = 0,
                    HighWaterMarkMultiplier = 1f,
                }
            );
            NativeSharedAnchor.Register<SeedDescriptor, int>(
                env.Accessor,
                d => d.Seed + ++builds // impure: drifts per build
            );
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "SimSide");

            var ptr = NativeSharedPtr.Acquire<SeedDescriptor, int>(
                fixedAcc,
                new SeedDescriptor(10)
            );
            var id = ptr.GetBlobId(fixedAcc);
            ptr.Dispose(fixedAcc);

            NAssert.Throws<TrecsException>(() => NativeSharedPtr.Acquire<int>(fixedAcc, id));
        }

        // The caller-supplied-id flavor of the same guard: reusing a hand-picked id for different
        // eager content across the blob's lifetime (alloc → evict → re-alloc with new bytes) is
        // the id-aliasing footgun; the content hash survives eviction and catches it.
        [Test]
        public void EagerIdReuse_DifferentContentAfterEviction_AssertsAtInsert()
        {
            using var env = CreateEnv(
                new BlobCacheSettings
                {
                    MaxInactiveNativeBlobsMb = 0f,
                    MaxInactiveManagedBlobsCount = 0,
                    HighWaterMarkMultiplier = 1f,
                }
            );
            var id = new BlobId(777);

            var anchor = SharedAnchor.Alloc(env.Accessor, id, "first-contents");
            anchor.Dispose(env.Accessor); // zero inactive cap → evicted and forgotten

            // Same hand-picked id, different bytes: would silently alias without the attestation.
            NAssert.Throws<TrecsException>(() =>
                SharedAnchor.Alloc(env.Accessor, id, "second-contents")
            );
        }

        // (Save-time journal completeness used to be testable here by punching a hole with
        // PruneJournal — drop a sim-held blob's descriptor, re-acquire by id, save. PruneJournal is
        // gone (the input-descriptor sweep is the only journal GC now, and it drops journal entry
        // and source together, only for ids the sim heaps cannot reference), so the hole is
        // structurally closed; the DEBUG assert in CollectJournaledDescriptors remains as backstop.)

        // (g) The B4 role gate: anchors are ambient holds, so every pin-creating/releasing anchor
        // entry point throws from a Fixed-role (deterministic simulation) accessor.
        [Test]
        public void AnchorLifecycle_FromFixedRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var id = new BlobId(7);
            SharedAnchor.Register(env.Accessor, id, "registered-payload");
            SharedAnchor.Register<SeedDescriptor, string>(env.Accessor, d => $"seed-{d.Seed}");
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "FixedAnchorGate");

            NAssert.Throws<TrecsException>(() => SharedAnchor.Acquire<string>(fixedAcc, id));
            NAssert.Throws<TrecsException>(() =>
                SharedAnchor.Acquire<SeedDescriptor, string>(fixedAcc, new SeedDescriptor(1))
            );
            NAssert.Throws<TrecsException>(() =>
                SharedAnchor.TryAcquire<string>(fixedAcc, id, out _)
            );
            NAssert.Throws<TrecsException>(() => SharedAnchor.Alloc(fixedAcc, "eager-payload"));

            // Clone / Dispose are lifecycle ops too: acquire ambiently, then verify the Fixed-role
            // accessor can neither clone nor release the pin.
            var anchor = SharedAnchor.Acquire<string>(env.Accessor, id);
            NAssert.Throws<TrecsException>(() => anchor.Clone(fixedAcc));
            NAssert.Throws<TrecsException>(() => anchor.Dispose(fixedAcc));

            // Reads and probes are gated too: a Fixed accessor holding an anchor at all means data
            // crossed a domain boundary outside the input stream, and CanGet/TryGet/IsResident are
            // residency/liveness probes — non-deterministic state the sim must not branch on.
            NAssert.Throws<TrecsException>(() => anchor.Get(fixedAcc));
            NAssert.Throws<TrecsException>(() => anchor.TryGet(fixedAcc, out _));
            NAssert.Throws<TrecsException>(() => anchor.CanGet(fixedAcc));
            NAssert.Throws<TrecsException>(() => SharedAnchor.IsResident(fixedAcc, id));

            // The same reads remain available to every ambient role.
            NAssert.AreEqual("registered-payload", anchor.Get(env.Accessor));
            NAssert.IsTrue(SharedAnchor.IsResident(env.Accessor, id));

            anchor.Dispose(env.Accessor);
        }

        // Native mirror of the (g) gate — one representative entry point per category.
        [Test]
        public void NativeAnchorLifecycle_FromFixedRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var id = new BlobId(8);
            NativeSharedAnchor.Register(env.Accessor, id, 1234);
            var fixedAcc = env.World.CreateAccessor(AccessorRole.Fixed, "FixedNativeAnchorGate");

            NAssert.Throws<TrecsException>(() => NativeSharedAnchor.Acquire<int>(fixedAcc, id));
            NAssert.Throws<TrecsException>(() =>
                NativeSharedAnchor.TryAcquire<int>(fixedAcc, id, out _)
            );
            NAssert.Throws<TrecsException>(() => NativeSharedAnchor.Alloc(fixedAcc, 5678));

            var anchor = NativeSharedAnchor.Acquire<int>(env.Accessor, id);
            NAssert.Throws<TrecsException>(() => anchor.Clone(fixedAcc));
            NAssert.Throws<TrecsException>(() => anchor.Dispose(fixedAcc));

            // Reads and probes gated too — see the managed-anchor test for the rationale.
            NAssert.Throws<TrecsException>(() => anchor.Get(fixedAcc));
            NAssert.Throws<TrecsException>(() => anchor.CanGet(fixedAcc));
            NAssert.AreEqual(1234, anchor.Get(env.Accessor));

            anchor.Dispose(env.Accessor);
        }
    }
}
