using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Focused unit tests for <see cref="BundleRecorder"/> and
    /// <see cref="BundlePlayer"/> that don't depend on the long-running
    /// determinism canary in <see cref="RecordingRoundTripTests"/>. Each test
    /// drives a small world directly and steps a handful of frames so the
    /// individual API contracts are verifiable in isolation.
    /// </summary>
    [TestFixture]
    public class BundleRecorderPlayerTests
    {
        const int Version = 1;
        const ulong RngSeed = 0x123456789ABCDEF0ul;

        [Test]
        public void Recorder_StartPopulatesInitialSnapshotAndChecksum()
        {
            // Start captures an initial snapshot + non-zero checksum so the
            // bundle is self-contained. We don't expose those fields directly
            // off the recorder, so verify them via the bundle returned by an
            // immediate Stop (no intervening fixed updates).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            env.Accessor.SubmitEntities();

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

            var bundle = recorder.Stop();

            NAssert.IsNotNull(bundle.InitialSnapshot, "InitialSnapshot bytes should be populated");
            NAssert.Greater(
                bundle.InitialSnapshot.Length,
                0,
                "InitialSnapshot should not be empty"
            );
            NAssert.AreNotEqual(
                0u,
                bundle.InitialSnapshotChecksum,
                "InitialSnapshotChecksum should be non-zero after Start"
            );

            // Recompute the checksum independently and confirm it matches the
            // value the recorder stored on the bundle.
            using var checksumBuffer = new SerializationBuffer(registry);
            var checksumCalc = new RecordingChecksumCalculator(worldStateSer);
            var recomputed = checksumCalc.CalculateCurrentChecksum(
                version: Version,
                checksumBuffer,
                settings.ChecksumFlags
            );
            NAssert.AreEqual(
                bundle.InitialSnapshotChecksum,
                recomputed,
                "Recomputed initial-state checksum should match the stored value"
            );
        }

        [Test]
        public void Recorder_StopWithoutTicksProducesEmptyBundle()
        {
            // Start → Stop with zero stepped frames produces a bundle whose
            // start frame == end frame, with one initial snapshot and zero
            // anchors / snapshots. The Checksums dict still contains the
            // start-frame entry (recorded by Start so playback can verify
            // post-LoadInitialState matches).
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            env.Accessor.SubmitEntities();

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

            var startFrame = env.World.FixedFrame;
            recorder.Start();
            var bundle = recorder.Stop();

            NAssert.AreEqual(
                bundle.Header.StartFixedFrame,
                bundle.Header.EndFixedFrame,
                "End frame should equal start frame when no ticks ran"
            );
            NAssert.AreEqual(startFrame, bundle.Header.StartFixedFrame);
            NAssert.IsNotNull(bundle.InitialSnapshot);
            NAssert.Greater(bundle.InitialSnapshot.Length, 0);
            NAssert.AreEqual(0, bundle.Anchors.Count, "Should have no anchors");
            NAssert.AreEqual(0, bundle.Snapshots.Count, "Should have no snapshots");
        }

        [Test]
        public void Recorder_CapturesAnchorsAtConfiguredCadence()
        {
            // BundleRecorder anchors fire when (frame - lastAnchorFrame) *
            // fixedDeltaTime >= AnchorIntervalSeconds. Default fixedDeltaTime
            // is 1/60s, so an interval of 3 frames * (1/60) = 0.05s should
            // yield ~3 anchors over 10 stepped frames.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            var settings = new BundleRecorderSettings
            {
                Version = Version,
                AnchorIntervalSeconds = 3f / 60f, // every 3 fixed frames
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

            // Unlike TrecsAutoRecorder there is no forced first anchor — the
            // first cadence-driven anchor lands once the elapsed time crosses
            // the interval.
            NAssert.AreEqual(0, recorder.Anchors.Count, "No anchors before any frames have ticked");

            env.StepFixedFrames(10);

            // Cadence: anchors at frames startFrame+3, +6, +9 → at least 3.
            NAssert.GreaterOrEqual(
                recorder.Anchors.Count,
                3,
                "Anchor cadence should produce multiple anchors over 10 frames at 3-frame cadence"
            );

            // Anchors should be strictly increasing in frame.
            for (int i = 1; i < recorder.Anchors.Count; i++)
            {
                NAssert.Less(
                    recorder.Anchors[i - 1].FixedFrame,
                    recorder.Anchors[i].FixedFrame,
                    $"Anchor[{i - 1}].FixedFrame should be < Anchor[{i}].FixedFrame"
                );
            }

            // Adjacent anchors should be spaced by at least the configured
            // cadence (in frames).
            const int minFrameGap = 3;
            for (int i = 1; i < recorder.Anchors.Count; i++)
            {
                var gap = recorder.Anchors[i].FixedFrame - recorder.Anchors[i - 1].FixedFrame;
                NAssert.GreaterOrEqual(
                    gap,
                    minFrameGap,
                    $"Anchor gap at index {i} should be >= {minFrameGap} frames"
                );
            }
        }

        [Test]
        public void Player_StartRejectsBundleWithoutInitialSnapshot()
        {
            // BundlePlayer.Start treats an empty / null InitialSnapshot as a
            // hard error — there's nothing to restore the world from.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            using var player = new BundlePlayer(env.World, worldStateSer, registry, snapshots);
            player.Initialize();

            // Null payload → reject.
            var nullBundle = new RecordingBundle
            {
                Header = MakeHeader(startFrame: 0, endFrame: 10),
                InitialSnapshot = null,
                InputQueue = Array.Empty<byte>(),
                Checksums = new DenseDictionary<int, uint>(),
                Anchors = Array.Empty<BundleAnchor>(),
                Snapshots = Array.Empty<BundleSnapshot>(),
            };
            NAssert.Throws<InvalidOperationException>(() => player.Start(nullBundle));

            // Empty payload → reject.
            var emptyBundle = new RecordingBundle
            {
                Header = MakeHeader(startFrame: 0, endFrame: 10),
                InitialSnapshot = Array.Empty<byte>(),
                InputQueue = Array.Empty<byte>(),
                Checksums = new DenseDictionary<int, uint>(),
                Anchors = Array.Empty<BundleAnchor>(),
                Snapshots = Array.Empty<BundleSnapshot>(),
            };
            NAssert.Throws<InvalidOperationException>(() => player.Start(emptyBundle));

            NAssert.IsFalse(player.IsPlaying, "Failed Start should leave player Idle");
        }

        [Test]
        public void Player_TickReportsChecksumVerifiedOnRecordedFramesOnly()
        {
            // Build a bundle whose Checksums dict only covers some of the
            // playback frames; verify Tick reports ChecksumVerified=true on
            // recorded frames and false on unrecorded ones.
            //
            // Note: BundleRecorder's per-frame checksum cadence is gated on
            // !TRECS_IS_PROFILING, so when that define is set the recorder
            // produces only the start-frame checksum. To keep this test
            // exercising the BundlePlayer.Tick API regardless of build flags,
            // we capture checksums ourselves during the record phase via the
            // same RecordingChecksumCalculator the recorder uses, then merge
            // them into the bundle before playback.
            byte[] bundleBytes;
            var manualChecksums = new Dictionary<int, uint>();

            // ── Record ──────────────────────────────────────────────────────
            using (var env = CreateEnvWithCounterSystem())
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
                    // Recorder's own cadence is irrelevant — we manually
                    // capture every other frame below.
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

                // Capture checksums every other frame as the simulation runs,
                // matching the same point in the cycle the recorder would use
                // (after fixed update completes, before the next step).
                var checksumCalc = new RecordingChecksumCalculator(worldStateSer);
                using var checksumBuffer = new SerializationBuffer(registry);
                int frameNumber = 0;
                using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() =>
                {
                    frameNumber++;
                    if (frameNumber % 2 == 0)
                    {
                        var fixedFrame = env.World.FixedFrame;
                        manualChecksums[fixedFrame] = checksumCalc.CalculateCurrentChecksum(
                            version: Version,
                            checksumBuffer,
                            settings.ChecksumFlags
                        );
                    }
                });

                env.StepFixedFrames(6);

                var bundle = recorder.Stop();

                // Merge the manually-captured checksums into the bundle.
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
                manualChecksums.Count,
                1,
                "Test setup: manual checksum capture should have produced multiple entries"
            );

            // ── Playback ────────────────────────────────────────────────────
            using (var env = CreateEnvWithCounterSystem())
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

                int verifiedCount = 0;
                int unverifiedCount = 0;
                for (int i = 0; i < 6; i++)
                {
                    env.StepFixedFrames(1);
                    var result = player.Tick();
                    var currentFrame = env.World.FixedFrame;
                    var hasRecorded = bundle.Checksums.ContainsKey(currentFrame);

                    NAssert.AreEqual(
                        hasRecorded,
                        result.ChecksumVerified,
                        $"Tick at frame {currentFrame}: ChecksumVerified should match presence in recorded checksums"
                    );
                    if (result.ChecksumVerified)
                    {
                        verifiedCount++;
                        NAssert.IsTrue(
                            result.ExpectedChecksum.HasValue,
                            "ExpectedChecksum should be populated when verified"
                        );
                        NAssert.IsTrue(
                            result.ActualChecksum.HasValue,
                            "ActualChecksum should be populated when verified"
                        );
                    }
                    else
                    {
                        unverifiedCount++;
                        NAssert.IsFalse(
                            result.ExpectedChecksum.HasValue,
                            "ExpectedChecksum should be null when not verified"
                        );
                    }
                }

                NAssert.Greater(verifiedCount, 0, "Should have verified at least one frame");
                NAssert.Greater(unverifiedCount, 0, "Should have skipped at least one frame");
                NAssert.IsFalse(player.HasDesynced, "Honest round-trip should not desync");

                player.Stop();
            }
        }

        [Test]
        public void Player_HasDesyncedFlipsOnForcedMismatch()
        {
            // Mid-playback, mutate live entity state so the next computed
            // checksum cannot match the recorded one. HasDesynced should flip
            // to true; subsequent Ticks should remain in the desynced state
            // (no recovery, no thrashing).
            //
            // Same workaround as Player_TickReportsChecksumVerifiedOnRecordedFramesOnly:
            // capture every-frame checksums ourselves so the test exercises
            // the API regardless of TRECS_IS_PROFILING.
            byte[] bundleBytes;
            var manualChecksums = new Dictionary<int, uint>();

            using (var env = CreateEnvWithCounterSystem())
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

                env.StepFixedFrames(8);
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

            NAssert.Greater(
                manualChecksums.Count,
                1,
                "Test setup: manual checksum capture should have produced multiple entries"
            );

            using (var env = CreateEnvWithCounterSystem())
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

                NAssert.IsFalse(
                    player.HasDesynced,
                    "Player should not be desynced before any Ticks"
                );

                // Step one frame normally, then poison state, then Tick again.
                env.StepFixedFrames(1);
                player.Tick();
                NAssert.IsFalse(
                    player.HasDesynced,
                    "First tick should match the recorded checksum"
                );

                // Mutate every QId1 entity's TestInt to a value the recording
                // can't have produced.
                foreach (var ei in env.Accessor.Query().WithTags(Tag<QId1>.Value).Indices())
                {
                    env.Accessor.Component<TestInt>(ei).Write.Value = 999999;
                }
                env.Accessor.SubmitEntities();

                env.StepFixedFrames(1);
                player.Tick();
                NAssert.IsTrue(
                    player.HasDesynced,
                    "HasDesynced should flip to true after forced mismatch"
                );
                NAssert.IsTrue(
                    player.DesyncedFrame.HasValue,
                    "DesyncedFrame should be populated after a desync"
                );
                NAssert.AreEqual(
                    BundlePlaybackState.Desynced,
                    player.State,
                    "State should be Desynced after forced mismatch"
                );

                // Once desynced, further Ticks return default (no further
                // verification).
                env.StepFixedFrames(1);
                var staleResult = player.Tick();
                NAssert.IsFalse(
                    staleResult.ChecksumVerified,
                    "Tick after desync should return default (no verification)"
                );
                NAssert.IsTrue(player.HasDesynced, "Player remains desynced");

                player.Stop();
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────────

        static BundleHeader MakeHeader(int startFrame, int endFrame)
        {
            return new BundleHeader
            {
                Version = Version,
                StartFixedFrame = startFrame,
                EndFixedFrame = endFrame,
                FixedDeltaTime = 1f / 60f,
                ChecksumFlags = 0L,
                BlobIds = new DenseHashSet<BlobId>(),
            };
        }

        static TestEnvironment CreateEnvWithCounterSystem()
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
            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestFloat { Value = i * 0.25f })
                    .AssertComplete();
            }
            a.SubmitEntities();
        }
    }
}
