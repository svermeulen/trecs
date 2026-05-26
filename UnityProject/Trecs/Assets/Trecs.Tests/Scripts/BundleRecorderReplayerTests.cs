#pragma warning disable TRECS128
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
    /// Minimal Input-phase system used to verify that
    /// <see cref="BundleReplayer"/> only toggles Input-phase systems on its
    /// <see cref="EnableChannel.Playback"/> channel — and, on the failure
    /// path tested below, that it leaves them untouched. Body is empty: we
    /// only care that the runner has *something* on the Input phase to flip.
    /// </summary>
    [ExecuteIn(SystemPhase.Input)]
    partial class BundleReplayerTestInputSystem : ISystem
    {
        public void Execute() { }
    }

    /// <summary>
    /// Focused unit tests for <see cref="BundleRecorder"/> and
    /// <see cref="BundleReplayer"/> that don't depend on the long-running
    /// determinism canary in <see cref="RecordingRoundTripTests"/>. Each test
    /// drives a small world directly and steps a handful of frames so the
    /// individual API contracts are verifiable in isolation.
    /// </summary>
    [TestFixture]
    public class BundleRecorderReplayerTests
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
            env.Accessor.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var pool = new SnapshotPayloadPool();
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World, pool);
            var settings = new BundleRecorderSettings
            {
                Version = Version,
                AnchorIntervalSeconds = 1000f,
            };
            using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);
            recorder.Start();

            var bundle = recorder.Stop();

            NAssert.Greater(
                bundle.InitialSnapshot.Length,
                0,
                "InitialSnapshot should not be empty"
            );
            var storedInitialChecksum = bundle.Checksums[bundle.Header.StartFixedFrame];
            NAssert.AreNotEqual(
                0UL,
                storedInitialChecksum,
                "Initial-frame checksum in bundle.Checksums should be non-zero after Start"
            );

            // Recompute the checksum independently and confirm it matches the
            // value the recorder stored on the bundle.
            var recomputed = snapshots.ComputeChecksum(Version, includeTypeChecks: true);
            NAssert.AreEqual(
                storedInitialChecksum,
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
            env.Accessor.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var pool = new SnapshotPayloadPool();
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World, pool);
            var settings = new BundleRecorderSettings
            {
                Version = Version,
                AnchorIntervalSeconds = 1000f,
            };
            using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);

            var startFrame = env.World.FixedFrame;
            recorder.Start();
            var bundle = recorder.Stop();

            NAssert.AreEqual(
                bundle.Header.StartFixedFrame,
                bundle.Header.EndFixedFrame,
                "End frame should equal start frame when no ticks ran"
            );
            NAssert.AreEqual(startFrame, bundle.Header.StartFixedFrame);
            NAssert.Greater(bundle.InitialSnapshot.Length, 0);
            NAssert.AreEqual(0, bundle.Anchors.Count, "Should have no anchors");
            NAssert.AreEqual(0, bundle.Bookmarks.Count, "Should have no bookmarks");
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
            env.Accessor.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var pool = new SnapshotPayloadPool();
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World, pool);
            var settings = new BundleRecorderSettings
            {
                Version = Version,
                AnchorIntervalSeconds = 3f / 60f, // every 3 fixed frames
            };
            using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);
            recorder.Start();

            // Unlike TrecsRewindBuffer there is no forced first anchor — the
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
            // BundleRereplayer.Start treats an empty / null InitialSnapshot as a
            // hard error — there's nothing to restore the world from.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var pool = new SnapshotPayloadPool();
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World, pool);
            using var replayer = new BundleReplayer(env.World, registry, snapshots);
            replayer.Initialize();

            // Empty payload → reject. ReadOnlyMemory<byte> is a struct with no
            // null state; default-valued (== Empty) and explicit Array.Empty
            // both exercise the same "no initial snapshot" path.
            var defaultBundle = new RecordingBundle
            {
                Header = MakeHeader(startFrame: 0, endFrame: 10),
                InputQueue = Array.Empty<byte>(),
                Checksums = new IterableDictionary<int, ulong>(),
                Anchors = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
            };
            NAssert.Throws<InvalidOperationException>(() => replayer.Start(defaultBundle));

            var emptyBundle = new RecordingBundle
            {
                Header = MakeHeader(startFrame: 0, endFrame: 10),
                InitialSnapshot = Array.Empty<byte>(),
                InputQueue = Array.Empty<byte>(),
                Checksums = new IterableDictionary<int, ulong>(),
                Anchors = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
            };
            NAssert.Throws<InvalidOperationException>(() => replayer.Start(emptyBundle));

            NAssert.IsFalse(replayer.IsPlaying, "Failed Start should leave replayer Idle");
        }

        [Test]
        public void Player_StartFailureAfterLoadSnapshotResetsPlayerCleanly()
        {
            // Covers BundleRereplayer.Start's transactional-failure path: once
            // LoadSnapshot has run the live world is mutated and cannot be
            // rolled back, but the *replayer surface* must still revert to a
            // clean Idle so a follow-up Start can be attempted without
            // dragging half-installed state forward. Failure-trigger of
            // choice is a deliberately-wrong InitialSnapshotChecksum:
            // LoadSnapshot succeeds (the bytes are valid), then
            // VerifyPostDeserializationChecksum throws inside the try.
            //
            // The four post-failure invariants under test:
            //   1. replayer.State == Idle
            //   2. replayer.Bundle == null
            //   3. Input-phase systems were NOT disabled on the Playback
            //      channel (no leftover SetInputSystemsEnabled(false)).
            //   4. The replayer was NOT left registered as an input-history
            //      locker. We assert (4) indirectly via a follow-up
            //      Start(goodBundle): if the locker were still installed,
            //      EntityInputQueue.AddHistoryLocker's debug-assert would
            //      fire on the duplicate add. (The follow-up Start also
            //      cross-checks that Bundle/State are usable again.)
            using var env = EcsTestHelper.CreateEnvironment(
                new WorldSettings { RandomSeed = RngSeed },
                b => b.AddSystem(new BundleReplayerTestInputSystem()),
                TestTemplates.SimpleAlpha
            );
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
            env.Accessor.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            using var pool = new SnapshotPayloadPool();
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World, pool);
            var settings = new BundleRecorderSettings
            {
                Version = Version,
                AnchorIntervalSeconds = 1000f,
            };

            // Produce a valid bundle via the recorder so InitialSnapshot is
            // round-trippable. We then construct a sibling bundle whose
            // start-frame checksum has been replaced with a bogus value to
            // drive the failure path inside VerifyPostDeserializationChecksum.
            RecordingBundle goodBundle;
            using (var recorder = new BundleRecorder(env.World, registry, settings, snapshots))
            {
                recorder.Start();
                goodBundle = recorder.Stop();
            }

            NAssert.Greater(goodBundle.InitialSnapshot.Length, 0);
            var goodInitialChecksum = goodBundle.Checksums[goodBundle.Header.StartFixedFrame];
            NAssert.AreNotEqual(0UL, goodInitialChecksum);

            // Same payload + header, but with the start-frame checksum
            // flipped so post-load verification cannot match.
            const ulong WrongChecksum = 0xDEADBEEFDEADBEEFul;
            NAssert.AreNotEqual(
                goodInitialChecksum,
                WrongChecksum,
                "Sanity: bogus checksum must actually differ from the recorded one"
            );
            var poisonedChecksums = WorldSnapshotListUtil.CopyChecksums(goodBundle.Checksums);
            poisonedChecksums[goodBundle.Header.StartFixedFrame] = WrongChecksum;
            var poisonedBundle = new RecordingBundle
            {
                Header = goodBundle.Header,
                InitialSnapshot = goodBundle.InitialSnapshot,
                InputQueue = goodBundle.InputQueue,
                Checksums = poisonedChecksums,
                Anchors = goodBundle.Anchors,
                Bookmarks = goodBundle.Bookmarks,
            };

            using var replayer = new BundleReplayer(env.World, registry, snapshots);
            replayer.Initialize();

            // Snapshot input-phase enabled state before the failed Start so
            // we can assert SetInputSystemsEnabled(false) didn't leak. The
            // accessor used here is independent of the replayer.s accessor;
            // both observe the same underlying SystemEnableState.
            var inputSystemIndices = new List<int>();
            var systems = env.World.GetSystems();
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i].Phase == SystemPhase.Input)
                {
                    inputSystemIndices.Add(i);
                }
            }
            NAssert.Greater(
                inputSystemIndices.Count,
                0,
                "Test setup: world should contain at least one Input-phase system"
            );
            foreach (var i in inputSystemIndices)
            {
                NAssert.IsTrue(
                    env.Accessor.IsSystemEnabled(i, EnableChannel.Playback),
                    $"Pre-Start: input system {i} should be enabled on Playback channel"
                );
            }

            // Drive the failure. LoadSnapshot succeeds (payload is valid),
            // then VerifyPostDeserializationChecksum throws — landing us in
            // the catch block that resets _state/_bundle and rethrows.
            NAssert.Throws<SerializationException>(() => replayer.Start(poisonedBundle));

            // (1) State surface fully reverted.
            NAssert.AreEqual(
                BundlePlaybackState.Idle,
                replayer.State,
                "After failed Start, rereplayer.State should be Idle"
            );
            NAssert.IsFalse(
                replayer.IsPlaying,
                "After failed Start, replayer.IsPlaying should be false"
            );

            // (2) Bundle reference cleared — no lingering pointer to the
            // partially-applied bundle.
            NAssert.IsNull(replayer.Bundle, "After failed Start, replayer.Bundle should be null");

            // (3) Input systems remain enabled. The Playback-channel toggle
            // happens AFTER VerifyPostDeserializationChecksum inside the
            // try, so it should never have run.
            foreach (var i in inputSystemIndices)
            {
                NAssert.IsTrue(
                    env.Accessor.IsSystemEnabled(i, EnableChannel.Playback),
                    $"After failed Start: input system {i} must remain enabled on Playback "
                        + "(no leftover SetInputSystemsEnabled(false) from the failed Start)"
                );
            }

            // (4) Locker not installed. Proven via a follow-up Start with a
            // good bundle: if the replayer were still registered on the
            // queue's history-locker list, AddHistoryLocker's
            // TrecsDebugAssert(!Contains(locker)) would fire on the
            // duplicate add. (TrecsDebugAssert is stripped from release —
            // see CLAUDE.md — but this test runs under EditMode where it's
            // live.) A clean follow-up Start additionally proves the
            // overall surface is reusable.
            NAssert.DoesNotThrow(
                () => replayer.Start(goodBundle),
                "Follow-up Start(goodBundle) should succeed cleanly — proves the "
                    + "failed Start left no locker registered and no other half-state behind"
            );
            NAssert.AreEqual(BundlePlaybackState.Playing, replayer.State);
            NAssert.AreSame(goodBundle, replayer.Bundle);

            replayer.Stop();
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
            // exercising the BundleRereplayer.Tick API regardless of build flags,
            // we capture checksums ourselves during the record phase via
            // SnapshotSerializer.ComputeChecksum (the same path the recorder
            // uses), then merge them into the bundle before playback.
            byte[] bundleBytes;
            var manualChecksums = new Dictionary<int, ulong>();

            // ── Record ──────────────────────────────────────────────────────
            using (var env = CreateEnvWithCounterSystem())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var pool = new SnapshotPayloadPool();
                using var snapshots = new SnapshotSerializer(
                    worldStateSer,
                    registry,
                    env.World,
                    pool
                );
                var settings = new BundleRecorderSettings
                {
                    Version = Version,
                    AnchorIntervalSeconds = 1000f,
                    // Recorder's own cadence is irrelevant — we manually
                    // capture every other frame below.
                };
                using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);
                recorder.Start();

                // Capture checksums every other frame as the simulation runs,
                // matching the same point in the cycle the recorder would use
                // (after fixed update completes, before the next step).
                int frameNumber = 0;
                using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() =>
                {
                    frameNumber++;
                    if (frameNumber % 2 == 0)
                    {
                        var fixedFrame = env.World.FixedFrame;
                        manualChecksums[fixedFrame] = snapshots.ComputeChecksum(
                            Version,
                            includeTypeChecks: true
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
                using var pool = new SnapshotPayloadPool();
                using var snapshots = new SnapshotSerializer(
                    worldStateSer,
                    registry,
                    env.World,
                    pool
                );
                using var bundleSer = new RecordingBundleSerializer(registry);
                using var replayer = new BundleReplayer(env.World, registry, snapshots);
                replayer.Initialize();

                using var stream = new MemoryStream(bundleBytes);
                var bundle = bundleSer.Load(stream);
                replayer.Start(bundle);

                int verifiedCount = 0;
                int unverifiedCount = 0;
                for (int i = 0; i < 6; i++)
                {
                    env.StepFixedFrames(1);
                    var result = replayer.Tick();
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
                NAssert.IsFalse(replayer.HasDesynced, "Honest round-trip should not desync");

                replayer.Stop();
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
            // capture every-frame checksums ourselves via SnapshotSerializer.ComputeChecksum
            // so the test exercises the API regardless of TRECS_IS_PROFILING.
            byte[] bundleBytes;
            var manualChecksums = new Dictionary<int, ulong>();

            using (var env = CreateEnvWithCounterSystem())
            {
                SpawnEntities(env);

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                using var pool = new SnapshotPayloadPool();
                using var snapshots = new SnapshotSerializer(
                    worldStateSer,
                    registry,
                    env.World,
                    pool
                );
                var settings = new BundleRecorderSettings
                {
                    Version = Version,
                    AnchorIntervalSeconds = 1000f,
                };
                using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);
                recorder.Start();

                using var sub = env.Accessor.Events.OnFixedUpdateCompleted(() =>
                {
                    var fixedFrame = env.World.FixedFrame;
                    manualChecksums[fixedFrame] = snapshots.ComputeChecksum(
                        Version,
                        includeTypeChecks: true
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
                using var pool = new SnapshotPayloadPool();
                using var snapshots = new SnapshotSerializer(
                    worldStateSer,
                    registry,
                    env.World,
                    pool
                );
                using var bundleSer = new RecordingBundleSerializer(registry);
                using var replayer = new BundleReplayer(env.World, registry, snapshots);
                replayer.Initialize();

                using var stream = new MemoryStream(bundleBytes);
                var bundle = bundleSer.Load(stream);
                replayer.Start(bundle);

                NAssert.IsFalse(
                    replayer.HasDesynced,
                    "Player should not be desynced before any Ticks"
                );

                // Step one frame normally, then poison state, then Tick again.
                env.StepFixedFrames(1);
                replayer.Tick();
                NAssert.IsFalse(
                    replayer.HasDesynced,
                    "First tick should match the recorded checksum"
                );

                // Mutate every QId1 entity's TestInt to a value the recording
                // can't have produced.
                foreach (var ei in env.Accessor.Query().WithTags(Tag<QId1>.Value).Indices())
                {
                    env.Accessor.Component<TestInt>(ei).Write.Value = 999999;
                }
                env.Accessor.Submit();

                env.StepFixedFrames(1);
                replayer.Tick();
                NAssert.IsTrue(
                    replayer.HasDesynced,
                    "HasDesynced should flip to true after forced mismatch"
                );
                NAssert.IsTrue(
                    replayer.DesyncedFrame.HasValue,
                    "DesyncedFrame should be populated after a desync"
                );
                NAssert.AreEqual(
                    BundlePlaybackState.Desynced,
                    replayer.State,
                    "State should be Desynced after forced mismatch"
                );

                // Once desynced, further Ticks return default (no further
                // verification).
                env.StepFixedFrames(1);
                var staleResult = replayer.Tick();
                NAssert.IsFalse(
                    staleResult.ChecksumVerified,
                    "Tick after desync should return default (no verification)"
                );
                NAssert.IsTrue(replayer.HasDesynced, "Player remains desynced");

                replayer.Stop();
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
                BlobIds = new IterableHashSet<BlobId>(),
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
            a.Submit();
        }
    }
}
