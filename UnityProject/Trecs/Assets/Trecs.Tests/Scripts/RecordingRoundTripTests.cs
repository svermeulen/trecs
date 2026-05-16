using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Deterministic fixed-update system used by the record/playback tests. Each
    /// tick, every <c>QId1</c> entity's <see cref="TestInt"/> gets a pseudo-random
    /// increment from <c>World.Rng</c>. Because the RNG is seeded from
    /// <see cref="WorldSettings.RandomSeed"/> and the seed is identical for record
    /// and playback, an honest serialization round-trip reproduces the exact same
    /// values frame-by-frame — giving us a single deterministic chain of state that
    /// the checksum comparison locks in.
    /// </summary>
    partial class RecordingRoundTripCounterSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(QId1))]
        void Tick(ref TestInt counter)
        {
            counter.Value += (int)(World.Rng.NextUint() & 0xFF);
        }

        public void Execute()
        {
            Tick();
        }
    }

    /// <summary>
    /// End-to-end record → playback determinism: runs a deterministic fixed-update
    /// simulation, records via <see cref="BundleRecorder"/>, persists the bundle
    /// through <see cref="RecordingBundleSerializer"/>, then replays the loaded
    /// bundle against a fresh world (seeded identically) via <see cref="BundlePlayer"/>
    /// and verifies no desync. This is the canary for the whole
    /// recording/playback story — any non-determinism in the simulation, the
    /// checksum path, or the bundle's wire format surfaces as
    /// <see cref="BundlePlayer.HasDesynced"/>.
    ///
    /// <para>
    /// The recorder's per-frame checksum cadence is gated on <c>!TRECS_IS_PROFILING</c>,
    /// which is defined on Standalone, so we can't rely on the recorder to populate
    /// <see cref="RecordingBundle.Checksums"/> on every build. To keep these tests
    /// running regardless of build flags, the tests subscribe to
    /// <c>OnFixedUpdateCompleted</c> themselves, capture checksums via the same
    /// <see cref="RecordingChecksumCalculator"/> the recorder uses, and merge
    /// the results into the bundle before playback. This mirrors the workaround
    /// in <see cref="BundleRecorderPlayerTests"/>.
    /// </para>
    /// </summary>
    [TestFixture]
    public class RecordingRoundTripTests
    {
        const int FramesToRun = 24;
        const int Version = 1;
        const ulong RngSeed = 0xFEEDC0FFEEBAD123ul;

        [Test]
        public void RecordThenPlayback_DeterministicSim_NoDesync()
        {
            byte[] bundleBytes;
            var manualChecksums = new Dictionary<int, uint>();

            // ── Record phase ────────────────────────────────────────────────────
            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
                var settings = new BundleRecorderSettings
                {
                    Version = Version,
                    // Anchors aren't needed for this short-frame round-trip;
                    // a long interval keeps the produced bundle minimal.
                    AnchorIntervalSeconds = 1000f,
                    // Recorder's own per-frame cadence is irrelevant — we
                    // capture every frame manually below to sidestep the
                    // TRECS_IS_PROFILING gate.
                    ChecksumFrameInterval = 1000,
                };
                using var recorder = new BundleRecorder(
                    env.World,
                    worldStateSer,
                    registry,
                    settings,
                    snapshots
                );
                recorder.Initialize();
                recorder.Start();

                var checksumCalc = new RecordingChecksumCalculator(worldStateSer);
                using var checksumBuffer = new SerializationBuffer(registry);
                using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() =>
                {
                    var fixedFrame = env.World.FixedFrame;
                    manualChecksums[fixedFrame] = checksumCalc.CalculateCurrentChecksum(
                        version: Version,
                        checksumBuffer,
                        settings.ChecksumFlags
                    );
                });

                env.StepFixedFrames(FramesToRun);

                var bundle = recorder.Stop();

                // Merge the manually-captured checksums into the bundle so
                // BundlePlayer has something to verify against on every frame.
                foreach (var kv in manualChecksums)
                {
                    bundle.Checksums[kv.Key] = kv.Value;
                }

                using var bundleSer = new RecordingBundleSerializer(registry);
                using var stream = new MemoryStream();
                bundleSer.Save(bundle, stream);
                bundleBytes = stream.ToArray();
            }

            NAssert.Greater(
                bundleBytes.Length,
                0,
                "Bundle produced no bytes — recorder/tick loop is broken."
            );
            NAssert.GreaterOrEqual(
                manualChecksums.Count,
                FramesToRun,
                "Test setup: should have captured a checksum on every recorded frame."
            );

            // ── Playback phase ──────────────────────────────────────────────────
            using (var env = CreateEnv())
            {
                // Note: the playback world is pre-populated with the SAME entities as
                // the recording world and then overwritten by the bundle's initial
                // snapshot. Starting from an identical template is the simplest path.
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
                using var bundleSer = new RecordingBundleSerializer(registry);
                using var player = new BundlePlayer(env.World, worldStateSer, registry, snapshots);
                player.Initialize();

                using var stream = new MemoryStream(bundleBytes);
                var bundle = bundleSer.Load(stream);
                player.Start(bundle);

                int verifiedCount = 0;
                for (int i = 0; i < FramesToRun; i++)
                {
                    env.StepFixedFrames(1);
                    var result = player.Tick();

                    NAssert.IsFalse(
                        player.HasDesynced,
                        $"Playback desynced at frame {i + 1}. "
                            + "Simulation is not deterministic, or the serialization round-trip dropped/mutated state."
                    );
                    if (result.ChecksumVerified)
                    {
                        verifiedCount++;
                    }
                }

                NAssert.Greater(
                    verifiedCount,
                    0,
                    "No frames were checksum-verified during playback — the manual-capture merge into the bundle isn't working."
                );

                player.Stop();
            }
        }

        [Test]
        public void Playback_DesyncsWhenStateIsMutated()
        {
            // Defensive: the "no desync" test above can only fail if mutation ISN'T
            // detected. This sibling test corrupts playback state mid-flight and
            // asserts the player actually notices — proving the check is live
            // even when the recording itself is honest.
            //
            // Same workaround as RecordThenPlayback_DeterministicSim_NoDesync:
            // capture every-frame checksums ourselves so the test exercises the
            // API regardless of TRECS_IS_PROFILING.
            byte[] bundleBytes;
            var manualChecksums = new Dictionary<int, uint>();

            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
                var settings = new BundleRecorderSettings
                {
                    Version = Version,
                    AnchorIntervalSeconds = 1000f,
                    ChecksumFrameInterval = 1000,
                };
                using var recorder = new BundleRecorder(
                    env.World,
                    worldStateSer,
                    registry,
                    settings,
                    snapshots
                );
                recorder.Initialize();
                recorder.Start();

                var checksumCalc = new RecordingChecksumCalculator(worldStateSer);
                using var checksumBuffer = new SerializationBuffer(registry);
                using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() =>
                {
                    var fixedFrame = env.World.FixedFrame;
                    manualChecksums[fixedFrame] = checksumCalc.CalculateCurrentChecksum(
                        version: Version,
                        checksumBuffer,
                        settings.ChecksumFlags
                    );
                });

                env.StepFixedFrames(FramesToRun);
                var bundle = recorder.Stop();

                foreach (var kv in manualChecksums)
                {
                    bundle.Checksums[kv.Key] = kv.Value;
                }

                using var bundleSer = new RecordingBundleSerializer(registry);
                using var stream = new MemoryStream();
                bundleSer.Save(bundle, stream);
                bundleBytes = stream.ToArray();
            }

            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
                using var bundleSer = new RecordingBundleSerializer(registry);
                using var player = new BundlePlayer(env.World, worldStateSer, registry, snapshots);
                player.Initialize();

                using var stream = new MemoryStream(bundleBytes);
                var bundle = bundleSer.Load(stream);
                player.Start(bundle);

                // Corrupt the state mid-playback — mutate entities so the next checksum
                // cannot possibly match.
                env.StepFixedFrames(1);
                foreach (var ei in env.Accessor.Query().WithTags(Tag<QId1>.Value).Indices())
                {
                    env.Accessor.Component<TestInt>(ei).Write.Value = 999999;
                }
                env.Accessor.SubmitEntities();

                for (int i = 1; i < FramesToRun; i++)
                {
                    env.StepFixedFrames(1);
                    player.Tick();
                    if (player.HasDesynced)
                        break;
                }

                NAssert.IsTrue(
                    player.HasDesynced,
                    "Playback should have detected the injected state corruption as a desync."
                );

                player.Stop();
            }
        }

        [Test]
        public void Bundle_SnapshotsSurviveSaveLoadAndRestoreState()
        {
            // Snapshots attach a labelled full-state snapshot to a recording. They
            // survive the bundle Save/Load round-trip and their payloads are
            // independent SnapshotSerializer streams that LoadSnapshot can replay
            // into a fresh world. This guards the labelled-checkpoint navigation
            // path that recorder UIs depend on, and exercises the
            // SnapshotSerializer.LoadSnapshot path against bytes that were embedded
            // in a bundle (rather than a standalone snapshot file).
            byte[] bundleBytes;
            int firstSnapshotFrame;
            int secondSnapshotFrame;
            int firstSnapshotEntityCount;
            int secondSnapshotEntityCount;

            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
                var settings = new BundleRecorderSettings
                {
                    Version = Version,
                    AnchorIntervalSeconds = 1000f,
                    ChecksumFrameInterval = 1000,
                };
                using var recorder = new BundleRecorder(
                    env.World,
                    worldStateSer,
                    registry,
                    settings,
                    snapshots
                );
                recorder.Initialize();
                recorder.Start();

                env.StepFixedFrames(5);
                NAssert.IsTrue(recorder.CaptureSnapshotAtCurrentFrame("before-the-bug"));
                firstSnapshotFrame = env.World.FixedFrame;
                firstSnapshotEntityCount = env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value);

                // Add a fresh entity between the two snapshots so the second
                // snapshot's snapshot must be visibly different from the first.
                env.Accessor.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = 12345 })
                    .Set(new TestFloat { Value = 9.5f })
                    .AssertComplete();
                env.Accessor.SubmitEntities();

                env.StepFixedFrames(7);
                NAssert.IsTrue(recorder.CaptureSnapshotAtCurrentFrame("after-spawn"));
                secondSnapshotFrame = env.World.FixedFrame;
                secondSnapshotEntityCount = env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value);

                NAssert.Greater(
                    secondSnapshotEntityCount,
                    firstSnapshotEntityCount,
                    "Test setup: second snapshot should have more entities than the first."
                );

                env.StepFixedFrames(5);
                var bundle = recorder.Stop();

                NAssert.AreEqual(2, bundle.Snapshots.Count);

                using var bundleSer = new RecordingBundleSerializer(registry);
                using var stream = new MemoryStream();
                bundleSer.Save(bundle, stream);
                bundleBytes = stream.ToArray();
            }

            // Reload the bundle from bytes and verify the snapshots survived the
            // wire-format round trip with their payloads intact.
            {
                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                using var bundleSer = new RecordingBundleSerializer(registry);
                using var stream = new MemoryStream(bundleBytes);
                var loaded = bundleSer.Load(stream);

                NAssert.AreEqual(2, loaded.Snapshots.Count, "Snapshot count should round-trip");
                NAssert.AreEqual("before-the-bug", loaded.Snapshots[0].Label);
                NAssert.AreEqual("after-spawn", loaded.Snapshots[1].Label);
                NAssert.AreEqual(firstSnapshotFrame, loaded.Snapshots[0].FixedFrame);
                NAssert.AreEqual(secondSnapshotFrame, loaded.Snapshots[1].FixedFrame);
                NAssert.IsNotNull(loaded.Snapshots[0].Payload);
                NAssert.Greater(loaded.Snapshots[0].Payload.Length, 0);
                NAssert.IsNotNull(loaded.Snapshots[1].Payload);
                NAssert.Greater(loaded.Snapshots[1].Payload.Length, 0);

                // Verify each snapshot's snapshot payload restores the world to
                // the exact state captured at that frame. This exercises
                // SnapshotSerializer.LoadSnapshot against bundle-embedded bytes.
                using (var env = CreateEnv())
                {
                    SpawnEntities(env);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );

                    using var snapStream = new MemoryStream(loaded.Snapshots[0].Payload);
                    var meta = snapshots.LoadSnapshot(snapStream);
                    NAssert.AreEqual(firstSnapshotFrame, meta.FixedFrame);
                    NAssert.AreEqual(
                        firstSnapshotEntityCount,
                        env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value)
                    );
                }

                using (var env = CreateEnv())
                {
                    SpawnEntities(env);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );

                    using var snapStream = new MemoryStream(loaded.Snapshots[1].Payload);
                    var meta = snapshots.LoadSnapshot(snapStream);
                    NAssert.AreEqual(secondSnapshotFrame, meta.FixedFrame);
                    NAssert.AreEqual(
                        secondSnapshotEntityCount,
                        env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value)
                    );
                }
            }
        }

        [Test]
        public void Bundle_AnchorsSurviveSaveLoadAndRestoreState()
        {
            // Auto-anchors fire on the recorder's cadence and embed full-state
            // snapshots into the bundle for desync recovery and editor scrubbing.
            // Verify they survive the bundle Save/Load round-trip and that the
            // captured payloads can be replayed back into a world via
            // SnapshotSerializer.LoadSnapshot — same shape as the snapshot test
            // above but driven by the auto-cadence path rather than the manual
            // capture API.
            byte[] bundleBytes;
            int recorderAnchorCount;
            int firstAnchorFrame;
            int firstAnchorEntityCount;

            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
                var settings = new BundleRecorderSettings
                {
                    Version = Version,
                    // ~3-frame anchor cadence at default 1/60s fixed delta:
                    // 3 / 60 = 0.05s. Stepping FramesToRun (24) frames should
                    // produce ~8 anchors, plenty to verify the round-trip.
                    AnchorIntervalSeconds = 3f / 60f,
                    ChecksumFrameInterval = 1000,
                };
                using var recorder = new BundleRecorder(
                    env.World,
                    worldStateSer,
                    registry,
                    settings,
                    snapshots
                );
                recorder.Initialize();
                recorder.Start();

                env.StepFixedFrames(FramesToRun);

                NAssert.GreaterOrEqual(
                    recorder.Anchors.Count,
                    2,
                    "Test setup: anchor cadence should produce multiple anchors."
                );
                recorderAnchorCount = recorder.Anchors.Count;
                firstAnchorFrame = recorder.Anchors[0].FixedFrame;
                firstAnchorEntityCount = env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value);

                var bundle = recorder.Stop();
                NAssert.AreEqual(recorderAnchorCount, bundle.Anchors.Count);

                using var bundleSer = new RecordingBundleSerializer(registry);
                using var stream = new MemoryStream();
                bundleSer.Save(bundle, stream);
                bundleBytes = stream.ToArray();
            }

            // Reload and verify anchors survived bundle framing.
            {
                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                using var bundleSer = new RecordingBundleSerializer(registry);
                using var stream = new MemoryStream(bundleBytes);
                var loaded = bundleSer.Load(stream);

                NAssert.AreEqual(recorderAnchorCount, loaded.Anchors.Count);
                NAssert.AreEqual(firstAnchorFrame, loaded.Anchors[0].FixedFrame);
                NAssert.IsNotNull(loaded.Anchors[0].Payload);
                NAssert.Greater(loaded.Anchors[0].Payload.Length, 0);

                // Anchor frames must be strictly increasing — guards against any
                // wire-format reordering that would corrupt scrub-back navigation.
                for (int i = 1; i < loaded.Anchors.Count; i++)
                {
                    NAssert.Less(
                        loaded.Anchors[i - 1].FixedFrame,
                        loaded.Anchors[i].FixedFrame,
                        $"Anchor[{i - 1}].FixedFrame should be < Anchor[{i}].FixedFrame after Save/Load"
                    );
                }

                // Replay the first anchor's payload into a fresh world to confirm
                // the embedded snapshot bytes are intact end-to-end.
                using (var env = CreateEnv())
                {
                    // Don't pre-populate — anchor's snapshot should restore everything.
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );

                    NAssert.AreEqual(0, env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value));

                    using var snapStream = new MemoryStream(loaded.Anchors[0].Payload);
                    var meta = snapshots.LoadSnapshot(snapStream);
                    NAssert.AreEqual(firstAnchorFrame, meta.FixedFrame);
                    NAssert.AreEqual(
                        firstAnchorEntityCount,
                        env.Accessor.CountEntitiesWithTags(Tag<QId1>.Value)
                    );
                }
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        static TestEnvironment CreateEnv()
        {
            return EcsTestHelper.CreateEnvironment(
                new WorldSettings { RandomSeed = RngSeed },
                b => b.AddSystem(new RecordingRoundTripCounterSystem()),
                QTestEntityA.Template
            );
        }

        static void SpawnEntities(TestEnvironment env)
        {
            var a = env.Accessor;
            for (int i = 0; i < 5; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 100 })
                    .Set(new TestFloat { Value = i * 0.5f })
                    .AssertComplete();
            }
            a.SubmitEntities();
        }
    }
}
