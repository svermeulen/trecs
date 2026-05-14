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
    /// Round-trip tests for <see cref="RecordingBundle"/> and
    /// <see cref="RecordingBundleSerializer"/>. The bundle holds opaque payload
    /// bytes for snapshots and the input queue, so most tests verify framing
    /// and field round-trips with synthetic byte arrays — they don't depend on
    /// a live world. The world-integration test confirms that snapshot bytes
    /// captured via <see cref="SnapshotSerializer"/> survive the bundle envelope
    /// and can be loaded back into a world.
    /// </summary>
    [TestFixture]
    public class RecordingBundleTests
    {
        [Test]
        public void RoundTrip_MinimalBundle()
        {
            var registry = new SerializerRegistry();
            using var ser = new RecordingBundleSerializer(registry);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 7, startFrame: 0, endFrame: 60),
                InitialSnapshot = new byte[] { 1, 2, 3, 4, 5 },
                InputQueue = Array.Empty<byte>(),
                Checksums = new DenseDictionary<int, uint>(),
                Anchors = Array.Empty<BundleAnchor>(),
                Snapshots = Array.Empty<BundleSnapshot>(),
            };

            var roundTripped = RoundTripViaMemory(ser, original);

            AssertHeadersEqual(original.Header, roundTripped.Header);
            CollectionAssert.AreEqual(original.InitialSnapshot, roundTripped.InitialSnapshot);
            CollectionAssert.AreEqual(original.InputQueue, roundTripped.InputQueue);
            NAssert.AreEqual(0, roundTripped.Checksums.Count);
            NAssert.AreEqual(0, roundTripped.Anchors.Count);
            NAssert.AreEqual(0, roundTripped.Snapshots.Count);
        }

        [Test]
        public void RoundTrip_FullBundle()
        {
            var registry = new SerializerRegistry();
            using var ser = new RecordingBundleSerializer(registry);

            var checksums = new DenseDictionary<int, uint>();
            checksums.Add(0, 0xDEADBEEFu);
            checksums.Add(30, 0xCAFEF00Du);
            checksums.Add(60, 0xFEEDFACEu);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 3, startFrame: 0, endFrame: 60, fixedDelta: 1f / 60f),
                InitialSnapshot = MakeBytes(seed: 1, length: 200),
                InitialSnapshotChecksum = 0xABCD1234u,
                InputQueue = MakeBytes(seed: 2, length: 512),
                Checksums = checksums,
                Anchors = new List<BundleAnchor>
                {
                    new()
                    {
                        FixedFrame = 30,
                        Checksum = 0x11111111u,
                        Payload = MakeBytes(seed: 3, length: 128),
                    },
                    new()
                    {
                        FixedFrame = 60,
                        Checksum = 0x22222222u,
                        Payload = MakeBytes(seed: 4, length: 256),
                    },
                },
                Snapshots = new List<BundleSnapshot>
                {
                    new()
                    {
                        FixedFrame = 15,
                        Checksum = 0x33333333u,
                        Label = "before-the-bug",
                        Payload = MakeBytes(seed: 5, length: 64),
                    },
                    new()
                    {
                        FixedFrame = 45,
                        Checksum = 0x44444444u,
                        // Empty label: documented as the "unlabeled snapshot" sentinel.
                        Label = "",
                        Payload = MakeBytes(seed: 6, length: 96),
                    },
                },
            };

            var roundTripped = RoundTripViaMemory(ser, original);

            AssertHeadersEqual(original.Header, roundTripped.Header);
            CollectionAssert.AreEqual(original.InitialSnapshot, roundTripped.InitialSnapshot);
            NAssert.AreEqual(
                original.InitialSnapshotChecksum,
                roundTripped.InitialSnapshotChecksum
            );
            CollectionAssert.AreEqual(original.InputQueue, roundTripped.InputQueue);

            NAssert.AreEqual(3, roundTripped.Checksums.Count);
            NAssert.AreEqual(0xDEADBEEFu, roundTripped.Checksums[0]);
            NAssert.AreEqual(0xCAFEF00Du, roundTripped.Checksums[30]);
            NAssert.AreEqual(0xFEEDFACEu, roundTripped.Checksums[60]);

            NAssert.AreEqual(2, roundTripped.Anchors.Count);
            AssertAnchorsEqual(original.Anchors[0], roundTripped.Anchors[0]);
            AssertAnchorsEqual(original.Anchors[1], roundTripped.Anchors[1]);

            NAssert.AreEqual(2, roundTripped.Snapshots.Count);
            AssertSnapshotsEqual(original.Snapshots[0], roundTripped.Snapshots[0]);
            AssertSnapshotsEqual(original.Snapshots[1], roundTripped.Snapshots[1]);
        }

        [Test]
        public void RoundTrip_ViaFile()
        {
            var registry = new SerializerRegistry();
            using var ser = new RecordingBundleSerializer(registry);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 1, startFrame: 100, endFrame: 200),
                InitialSnapshot = MakeBytes(seed: 7, length: 64),
                InputQueue = MakeBytes(seed: 8, length: 32),
                Checksums = new DenseDictionary<int, uint>(),
                Anchors = Array.Empty<BundleAnchor>(),
                Snapshots = Array.Empty<BundleSnapshot>(),
            };

            var path = Path.Combine(
                Path.GetTempPath(),
                $"trecs_bundle_test_{Guid.NewGuid():N}.trec"
            );
            try
            {
                ser.Save(original, path);
                NAssert.IsTrue(File.Exists(path));

                var roundTripped = ser.Load(path);
                AssertHeadersEqual(original.Header, roundTripped.Header);
                CollectionAssert.AreEqual(original.InitialSnapshot, roundTripped.InitialSnapshot);
                CollectionAssert.AreEqual(original.InputQueue, roundTripped.InputQueue);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void PeekHeader_ReturnsHeaderWithoutFullLoad()
        {
            var registry = new SerializerRegistry();
            using var ser = new RecordingBundleSerializer(registry);

            var original = new RecordingBundle
            {
                Header = MakeHeader(
                    version: 42,
                    startFrame: 1000,
                    endFrame: 1500,
                    fixedDelta: 0.02f
                ),
                InitialSnapshot = MakeBytes(seed: 9, length: 1024),
                InputQueue = MakeBytes(seed: 10, length: 2048),
                Checksums = new DenseDictionary<int, uint>(),
                Anchors = Array.Empty<BundleAnchor>(),
                Snapshots = Array.Empty<BundleSnapshot>(),
            };

            using var saveStream = new MemoryStream();
            ser.Save(original, saveStream);

            saveStream.Position = 0;
            var peeked = ser.PeekHeader(saveStream);
            AssertHeadersEqual(original.Header, peeked);
        }

        [Test]
        public void Load_FailsOnEmptyStream()
        {
            var registry = new SerializerRegistry();
            using var ser = new RecordingBundleSerializer(registry);

            using var stream = new MemoryStream();
            NAssert.Throws<SerializationException>(() => ser.Load(stream));
        }

        [Test]
        public void Save_RejectsNullRequiredFields()
        {
            var registry = new SerializerRegistry();
            using var ser = new RecordingBundleSerializer(registry);
            using var stream = new MemoryStream();

            // Header missing
            NAssert.Throws<ArgumentNullException>(() =>
                ser.Save(new RecordingBundle { InitialSnapshot = Array.Empty<byte>() }, stream)
            );

            // InitialSnapshot missing
            NAssert.Throws<ArgumentNullException>(() =>
                ser.Save(new RecordingBundle { Header = MakeHeader(1, 0, 1) }, stream)
            );
        }

        [Test]
        public void RoundTrip_EmbeddedSnapshotSurvivesAndLoadsBackIntoWorld()
        {
            // End-to-end check: capture a real-world snapshot, embed its bytes
            // into a bundle, round-trip the bundle, then load the embedded
            // bytes back into a fresh world via SnapshotSerializer. If the
            // bundle's framing corrupted the inner payload, LoadSnapshot would
            // throw or restore the wrong state.
            byte[] snapshotBytes;
            using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
            {
                var a = env.Accessor;
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 99 }).AssertComplete();
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 17 }).AssertComplete();
                a.SubmitEntities();

                var registry = new SerializerRegistry();
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

                using var ms = new MemoryStream();
                snapshots.SaveSnapshot(version: 1, stream: ms);
                snapshotBytes = ms.ToArray();
            }

            // Wrap the snapshot bytes in a bundle and round-trip the bundle.
            var registry2 = new SerializerRegistry();
            using var bundleSer = new RecordingBundleSerializer(registry2);
            var bundle = new RecordingBundle
            {
                Header = MakeHeader(version: 1, startFrame: 0, endFrame: 0),
                InitialSnapshot = snapshotBytes,
                InputQueue = Array.Empty<byte>(),
                Checksums = new DenseDictionary<int, uint>(),
                Anchors = Array.Empty<BundleAnchor>(),
                Snapshots = Array.Empty<BundleSnapshot>(),
            };
            var loadedBundle = RoundTripViaMemory(bundleSer, bundle);

            // Now extract the bytes back out and feed them to a fresh world.
            using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
            {
                var a = env.Accessor;
                NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

                var registry = new SerializerRegistry();
                var worldStateSer = new WorldStateSerializer(env.World);
                using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);

                using var inner = new MemoryStream(loadedBundle.InitialSnapshot);
                snapshots.LoadSnapshot(inner);

                NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            }
        }

        [Test]
        public void TrecsAutoRecorder_RoundTripsViaFile()
        {
            // End-to-end: drive the editor-side recorder through Save and Load,
            // verifying the migration to RecordingBundle preserves the in-memory
            // snapshot list and restores world state correctly.
            var path = Path.Combine(
                Path.GetTempPath(),
                $"trecs_recorder_test_{Guid.NewGuid():N}.trec"
            );
            try
            {
                int snapshotCount;
                int firstFrame;
                int lastFrame;
                uint firstChecksum;

                // ── Record + Save ───────────────────────────────────────────
                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var a = env.Accessor;
                    a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
                    a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 88 }).AssertComplete();
                    a.SubmitEntities();

                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings
                    {
                        // Pick a small interval so a handful of stepped frames
                        // produce multiple captures.
                        AnchorIntervalSeconds = 0.05f,
                        Version = 1,
                    };
                    using var recorder = new TrecsAutoRecorder(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots
                    );
                    recorder.Initialize();
                    recorder.Start();

                    env.StepFixedFrames(20);

                    NAssert.GreaterOrEqual(
                        recorder.Anchors.Count,
                        2,
                        "Expected at least two anchors — capture is not running."
                    );

                    snapshotCount = recorder.Anchors.Count;
                    firstFrame = recorder.Anchors[0].FixedFrame;
                    lastFrame = recorder.Anchors[snapshotCount - 1].FixedFrame;
                    firstChecksum = recorder.Anchors[0].Checksum;

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                    NAssert.IsTrue(File.Exists(path));
                    NAssert.AreEqual(path, recorder.LoadedRecordingPath);
                }

                // ── Load into a fresh world ─────────────────────────────────
                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var a = env.Accessor;
                    NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings { Version = 1 };
                    using var recorder = new TrecsAutoRecorder(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots
                    );
                    recorder.Initialize();

                    NAssert.IsTrue(recorder.LoadRecordingFromFile(path));

                    // In-memory anchor list survived the round trip.
                    NAssert.AreEqual(snapshotCount, recorder.Anchors.Count);
                    NAssert.AreEqual(firstFrame, recorder.Anchors[0].FixedFrame);
                    NAssert.AreEqual(lastFrame, recorder.Anchors[snapshotCount - 1].FixedFrame);
                    NAssert.AreEqual(firstChecksum, recorder.Anchors[0].Checksum);

                    // World state was restored from the initial snapshot.
                    NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));

                    // Header peek works against the same file.
                    NAssert.IsTrue(TrecsAutoRecorder.TryReadRecordingHeader(path, out var header));
                    NAssert.AreEqual(firstFrame, header.StartFrame);
                    // EndFrame is the recording's save-time frame, which
                    // may exceed the last anchor when checksum / scrub-cache
                    // cadences fire past the final anchor — the last anchor
                    // just has to fall inside [StartFrame, EndFrame].
                    NAssert.GreaterOrEqual(header.EndFrame, lastFrame);
                }
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void TrecsAutoRecorder_CapturesAndRoundTripsSnapshots()
        {
            // Capture a couple of user snapshots during recording, save, load,
            // and verify the labels + frames + bytes survived. Snapshots are
            // independent of auto-captured anchors so this also exercises the
            // separate snapshot navigation path.
            var path = Path.Combine(
                Path.GetTempPath(),
                $"trecs_snapshot_test_{Guid.NewGuid():N}.trec"
            );
            try
            {
                int firstSnapshotFrame;
                int secondSnapshotFrame;

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var a = env.Accessor;
                    a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 5 }).AssertComplete();
                    a.SubmitEntities();

                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings
                    {
                        AnchorIntervalSeconds = 0.05f,
                        Version = 1,
                    };
                    using var recorder = new TrecsAutoRecorder(
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

                    env.StepFixedFrames(8);
                    NAssert.IsTrue(recorder.CaptureSnapshotAtCurrentFrame("after-mitigation"));
                    secondSnapshotFrame = env.World.FixedFrame;

                    NAssert.AreEqual(2, recorder.Snapshots.Count);
                    NAssert.AreEqual("before-the-bug", recorder.Snapshots[0].Label);
                    NAssert.AreEqual("after-mitigation", recorder.Snapshots[1].Label);
                    NAssert.Less(
                        recorder.Snapshots[0].FixedFrame,
                        recorder.Snapshots[1].FixedFrame
                    );

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                }

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings { Version = 1 };
                    using var recorder = new TrecsAutoRecorder(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots
                    );
                    recorder.Initialize();

                    NAssert.IsTrue(recorder.LoadRecordingFromFile(path));

                    NAssert.AreEqual(2, recorder.Snapshots.Count);
                    NAssert.AreEqual(firstSnapshotFrame, recorder.Snapshots[0].FixedFrame);
                    NAssert.AreEqual("before-the-bug", recorder.Snapshots[0].Label);
                    NAssert.AreEqual(secondSnapshotFrame, recorder.Snapshots[1].FixedFrame);
                    NAssert.AreEqual("after-mitigation", recorder.Snapshots[1].Label);

                    // Remove a snapshot and verify it's gone.
                    NAssert.IsTrue(recorder.RemoveSnapshotAtFrame(firstSnapshotFrame));
                    NAssert.AreEqual(1, recorder.Snapshots.Count);
                    NAssert.AreEqual("after-mitigation", recorder.Snapshots[0].Label);

                    // Removing a frame that no longer has a snapshot is a no-op.
                    NAssert.IsFalse(recorder.RemoveSnapshotAtFrame(firstSnapshotFrame));
                }
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void TrecsAutoRecorder_SnapshotOverwritesAtSameFrame()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            var settings = new TrecsAutoRecorderSettings
            {
                AnchorIntervalSeconds = 0.05f,
                Version = 1,
            };
            using var recorder = new TrecsAutoRecorder(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots
            );
            recorder.Initialize();
            recorder.Start();

            env.StepFixedFrames(3);

            NAssert.IsTrue(recorder.CaptureSnapshotAtCurrentFrame("first"));
            NAssert.IsTrue(recorder.CaptureSnapshotAtCurrentFrame("second"));

            // Same frame → second call replaces first, count stays at one.
            NAssert.AreEqual(1, recorder.Snapshots.Count);
            NAssert.AreEqual("second", recorder.Snapshots[0].Label);
        }

        [Test]
        public void TrecsAutoRecorder_CapturesAnchorAtCurrentFrame()
        {
            // Set the auto-anchor interval high so only the forced first
            // anchor would land via the cadence path; the manual call adds
            // one more. Verifies the manual API produces a regular anchor
            // in Anchors and that the cadence-timer reset suppresses any
            // redundant auto-anchor immediately after.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            var settings = new TrecsAutoRecorderSettings
            {
                // Long enough that the only auto-anchor is the forced first.
                AnchorIntervalSeconds = 1000f,
                ScrubCacheIntervalSeconds = 1000f,
                Version = 1,
            };
            using var recorder = new TrecsAutoRecorder(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots
            );
            recorder.Initialize();
            recorder.Start();

            env.StepFixedFrames(5);

            // Forced first auto-anchor lands on the first FixedUpdateCompleted
            // tick regardless of AnchorIntervalSeconds.
            NAssert.AreEqual(1, recorder.Anchors.Count);

            NAssert.IsTrue(recorder.CaptureAnchorAtCurrentFrame());
            var capturedFrame = env.World.FixedFrame;

            NAssert.AreEqual(2, recorder.Anchors.Count);
            NAssert.AreEqual(capturedFrame, recorder.Anchors[1].FixedFrame);

            // Step further; the cadence-timer reset means no new auto-anchor
            // fires (next one would be 1000s away).
            var countAfterManual = recorder.Anchors.Count;
            env.StepFixedFrames(5);
            NAssert.AreEqual(countAfterManual, recorder.Anchors.Count);
        }

        [Test]
        public void TrecsAutoRecorder_AnchorOverwritesAtSameFrame()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            var settings = new TrecsAutoRecorderSettings
            {
                AnchorIntervalSeconds = 1000f,
                ScrubCacheIntervalSeconds = 1000f,
                Version = 1,
            };
            using var recorder = new TrecsAutoRecorder(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots
            );
            recorder.Initialize();
            recorder.Start();

            env.StepFixedFrames(3);

            // Anchors[0] is the forced first auto-anchor; the two manual
            // captures land at the current frame, the second replacing the
            // first.
            var countBefore = recorder.Anchors.Count;
            NAssert.IsTrue(recorder.CaptureAnchorAtCurrentFrame());
            NAssert.IsTrue(recorder.CaptureAnchorAtCurrentFrame());

            // Manual + replace → only one new entry was added.
            NAssert.AreEqual(countBefore + 1, recorder.Anchors.Count);
        }

        [Test]
        public void BundleRecorder_CapturesAnchorAtCurrentFrame()
        {
            // Set the auto-anchor cadence so high it can't fire during the
            // test, then verify the manual capture API populates Anchors and
            // the captured anchor survives the bundle round-trip.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            var settings = new BundleRecorderSettings
            {
                Version = 1,
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

            env.StepFixedFrames(4);
            NAssert.AreEqual(0, recorder.Anchors.Count);

            NAssert.IsTrue(recorder.CaptureAnchorAtCurrentFrame());
            var capturedFrame = env.World.FixedFrame;

            NAssert.AreEqual(1, recorder.Anchors.Count);
            NAssert.AreEqual(capturedFrame, recorder.Anchors[0].FixedFrame);
            NAssert.IsNotNull(recorder.Anchors[0].Payload);
            NAssert.Greater(recorder.Anchors[0].Payload.Length, 0);

            // Double-call at the same frame replaces, doesn't append.
            NAssert.IsTrue(recorder.CaptureAnchorAtCurrentFrame());
            NAssert.AreEqual(1, recorder.Anchors.Count);

            env.StepFixedFrames(5);
            var bundle = recorder.Stop();

            NAssert.AreEqual(1, bundle.Anchors.Count);
            NAssert.AreEqual(capturedFrame, bundle.Anchors[0].FixedFrame);
        }

        [Test]
        public void TrecsAutoRecorder_ScrubCacheCapturesIndependentOfAnchors()
        {
            // Configure with sparse anchors and dense scrub cache. Stepping a
            // handful of frames should produce just the one initial anchor
            // but multiple scrub-cache entries, observable as
            // LastAnchorFrame outpacing the last anchor's frame.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            var worldStateSer = new WorldStateSerializer(env.World);
            using var snapshots = new SnapshotSerializer(worldStateSer, registry, env.World);
            var settings = new TrecsAutoRecorderSettings
            {
                AnchorIntervalSeconds = 1000f, // effectively never except the forced first
                ScrubCacheIntervalSeconds = 0.001f, // every frame
                Version = 1,
            };
            using var recorder = new TrecsAutoRecorder(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots
            );
            recorder.Initialize();
            recorder.Start();

            env.StepFixedFrames(10);

            // Just one anchor (the initial), but scrub cache has captured
            // additional frames so the live edge has moved past it.
            NAssert.AreEqual(1, recorder.Anchors.Count);
            NAssert.Greater(
                recorder.LastAnchorFrame,
                recorder.Anchors[0].FixedFrame,
                "Scrub cache should have advanced LastAnchorFrame past the initial anchor."
            );
        }

        [Test]
        public void TrecsAutoRecorder_ScrubCacheIsTransientAcrossSaveLoad()
        {
            // Round-trip a recording with a populated scrub cache. After load,
            // only the persisted anchors should remain — the transient scrub
            // cache is in-memory only and never written.
            var path = Path.Combine(
                Path.GetTempPath(),
                $"trecs_scrubcache_test_{Guid.NewGuid():N}.trec"
            );
            try
            {
                int anchorCountBeforeSave;
                int lastFrameBeforeSave;

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    env.Accessor.AddEntity(TestTags.Alpha)
                        .Set(new TestInt { Value = 1 })
                        .AssertComplete();
                    env.Accessor.SubmitEntities();

                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings
                    {
                        AnchorIntervalSeconds = 1000f,
                        ScrubCacheIntervalSeconds = 0.001f,
                        Version = 1,
                    };
                    using var recorder = new TrecsAutoRecorder(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots
                    );
                    recorder.Initialize();
                    recorder.Start();

                    env.StepFixedFrames(10);

                    anchorCountBeforeSave = recorder.Anchors.Count;
                    lastFrameBeforeSave = recorder.LastAnchorFrame;
                    NAssert.Greater(
                        lastFrameBeforeSave,
                        recorder.Anchors[0].FixedFrame,
                        "Test setup: scrub cache should have advanced LastAnchorFrame."
                    );

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                }

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings { Version = 1 };
                    using var recorder = new TrecsAutoRecorder(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots
                    );
                    recorder.Initialize();

                    NAssert.IsTrue(recorder.LoadRecordingFromFile(path));

                    NAssert.AreEqual(anchorCountBeforeSave, recorder.Anchors.Count);
                    // After load the scrub cache is empty, so LastAnchorFrame
                    // collapses back onto the last anchor's frame.
                    NAssert.AreEqual(
                        recorder.Anchors[recorder.Anchors.Count - 1].FixedFrame,
                        recorder.LastAnchorFrame
                    );
                }
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

#if TRECS_IS_PROFILING
        [Ignore(
            "TrecsAutoRecorder skips per-frame checksum writes when TRECS_IS_PROFILING is defined, so the round-trip check is a no-op."
        )]
#endif
        [Test]
        public void TrecsAutoRecorder_CapturesPerFrameChecksumsAndRoundTripsThem()
        {
            // Verify the editor recorder populates the bundle's Checksums dict
            // (matching BundleRecorder behaviour) so BundlePlayer's per-frame
            // desync detection works on editor-saved recordings. Without this
            // the saved Checksums dict was empty and desync detection only
            // fired at sparse anchor frames.
            var path = Path.Combine(
                Path.GetTempPath(),
                $"trecs_perframe_checksum_test_{Guid.NewGuid():N}.trec"
            );
            try
            {
                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    env.Accessor.AddEntity(TestTags.Alpha)
                        .Set(new TestInt { Value = 9 })
                        .AssertComplete();
                    env.Accessor.SubmitEntities();

                    var registry = new SerializerRegistry();
                    var worldStateSer = new WorldStateSerializer(env.World);
                    using var snapshots = new SnapshotSerializer(
                        worldStateSer,
                        registry,
                        env.World
                    );
                    var settings = new TrecsAutoRecorderSettings
                    {
                        // Sparse anchors so the per-frame cadence is the dominant
                        // checksum source. Sparse scrub cache for the same reason.
                        AnchorIntervalSeconds = 1000f,
                        ScrubCacheIntervalSeconds = 1000f,
                        // Capture every other frame — small enough to produce
                        // multiple captures over the test's StepFixedFrames(20)
                        // window without being so dense as to slow the test.
                        ChecksumFrameInterval = 2,
                        Version = 1,
                    };
                    using var recorder = new TrecsAutoRecorder(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots
                    );
                    recorder.Initialize();
                    recorder.Start();

                    env.StepFixedFrames(20);

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                }

                // Reopen the file and inspect the dict directly via the
                // serializer, since the recorder doesn't expose the dict.
                {
                    var registry = new SerializerRegistry();
                    using var bundleSer = new RecordingBundleSerializer(registry);
                    var bundle = bundleSer.Load(path);

                    NAssert.IsNotNull(
                        bundle.Checksums,
                        "Saved bundle should have a Checksums dict"
                    );
                    NAssert.Greater(
                        bundle.Checksums.Count,
                        1,
                        "Per-frame checksum cadence should produce multiple entries over 20 stepped frames"
                    );
                    // All captured frames should fall inside the recording's
                    // frame range.
                    foreach (var (frame, _) in bundle.Checksums)
                    {
                        NAssert.GreaterOrEqual(frame, bundle.Header.StartFixedFrame);
                        NAssert.LessOrEqual(frame, bundle.Header.EndFixedFrame);
                    }
                }
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        static RecordingBundle RoundTripViaMemory(
            RecordingBundleSerializer serializer,
            RecordingBundle original
        )
        {
            using var stream = new MemoryStream();
            serializer.Save(original, stream);
            stream.Position = 0;
            return serializer.Load(stream);
        }

        static BundleHeader MakeHeader(
            int version,
            int startFrame,
            int endFrame,
            float fixedDelta = 1f / 60f
        )
        {
            return new BundleHeader
            {
                Version = version,
                StartFixedFrame = startFrame,
                EndFixedFrame = endFrame,
                FixedDeltaTime = fixedDelta,
                ChecksumFlags = 0L,
                BlobIds = new DenseHashSet<BlobId>(),
            };
        }

        static byte[] MakeBytes(int seed, int length)
        {
            var rng = new Random(seed);
            var bytes = new byte[length];
            rng.NextBytes(bytes);
            return bytes;
        }

        static void AssertHeadersEqual(BundleHeader expected, BundleHeader actual)
        {
            NAssert.AreEqual(expected.Version, actual.Version, "Version");
            NAssert.AreEqual(expected.StartFixedFrame, actual.StartFixedFrame, "StartFixedFrame");
            NAssert.AreEqual(expected.EndFixedFrame, actual.EndFixedFrame, "EndFixedFrame");
            NAssert.AreEqual(expected.FixedDeltaTime, actual.FixedDeltaTime, "FixedDeltaTime");
            NAssert.AreEqual(expected.ChecksumFlags, actual.ChecksumFlags, "ChecksumFlags");
            NAssert.AreEqual(expected.BlobIds.Count, actual.BlobIds.Count, "BlobIds.Count");
        }

        static void AssertAnchorsEqual(BundleAnchor expected, BundleAnchor actual)
        {
            NAssert.AreEqual(expected.FixedFrame, actual.FixedFrame, "Anchor.FixedFrame");
            NAssert.AreEqual(expected.Checksum, actual.Checksum, "Anchor.Checksum");
            CollectionAssert.AreEqual(expected.Payload, actual.Payload, "Anchor.Payload");
        }

        static void AssertSnapshotsEqual(BundleSnapshot expected, BundleSnapshot actual)
        {
            NAssert.AreEqual(expected.FixedFrame, actual.FixedFrame, "Snapshot.FixedFrame");
            NAssert.AreEqual(expected.Checksum, actual.Checksum, "Snapshot.Checksum");
            NAssert.AreEqual(expected.Label, actual.Label, "Snapshot.Label");
            CollectionAssert.AreEqual(expected.Payload, actual.Payload, "Snapshot.Payload");
        }
    }
}
