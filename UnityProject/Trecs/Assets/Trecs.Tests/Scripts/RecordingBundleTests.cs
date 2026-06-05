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
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 7, startFrame: 0, endFrame: 60),
                InitialSnapshot = new byte[] { 1, 2, 3, 4, 5 },
                InputQueue = Array.Empty<byte>(),
                Checksums = new IterableDictionary<int, ulong>(),
                Keyframes = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
            };

            var roundTripped = RoundTripViaMemory(ser, original);

            AssertHeadersEqual(original.Header, roundTripped.Header);
            AssertPayloadsEqual(
                original.InitialSnapshot,
                roundTripped.InitialSnapshot,
                "InitialSnapshot"
            );
            AssertPayloadsEqual(original.InputQueue, roundTripped.InputQueue, "InputQueue");
            NAssert.AreEqual(0, roundTripped.Checksums.Count);
            NAssert.AreEqual(0, roundTripped.Keyframes.Count);
            NAssert.AreEqual(0, roundTripped.Bookmarks.Count);
        }

        [Test]
        public void RoundTrip_FullBundle()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

            var checksums = new IterableDictionary<int, ulong>();
            checksums.Add(0, 0xDEADBEEFDEADBEEFul);
            checksums.Add(30, 0xCAFEF00DCAFEF00Dul);
            checksums.Add(60, 0xFEEDFACEFEEDFACEul);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 3, startFrame: 0, endFrame: 60, fixedDelta: 1f / 60f),
                InitialSnapshot = MakeBytes(seed: 1, length: 200),
                InputQueue = MakeBytes(seed: 2, length: 512),
                Checksums = checksums,
                Keyframes = new List<WorldSnapshot>
                {
                    new()
                    {
                        FixedFrame = 30,
                        Kind = SnapshotKind.Keyframe,
                        Label = "",
                        Payload = MakeBytes(seed: 3, length: 128),
                    },
                    new()
                    {
                        FixedFrame = 60,
                        Kind = SnapshotKind.Keyframe,
                        Label = "",
                        Payload = MakeBytes(seed: 4, length: 256),
                    },
                },
                Bookmarks = new List<WorldSnapshot>
                {
                    new()
                    {
                        FixedFrame = 15,
                        Kind = SnapshotKind.Bookmark,
                        Label = "before-the-bug",
                        Payload = MakeBytes(seed: 5, length: 64),
                    },
                    new()
                    {
                        FixedFrame = 45,
                        Kind = SnapshotKind.Bookmark,
                        // Empty label: documented as the "unlabeled snapshot" sentinel.
                        Label = "",
                        Payload = MakeBytes(seed: 6, length: 96),
                    },
                },
            };

            var roundTripped = RoundTripViaMemory(ser, original);

            AssertHeadersEqual(original.Header, roundTripped.Header);
            AssertPayloadsEqual(
                original.InitialSnapshot,
                roundTripped.InitialSnapshot,
                "InitialSnapshot"
            );
            AssertPayloadsEqual(original.InputQueue, roundTripped.InputQueue, "InputQueue");

            NAssert.AreEqual(3, roundTripped.Checksums.Count);
            NAssert.AreEqual(0xDEADBEEFDEADBEEFul, roundTripped.Checksums[0]);
            NAssert.AreEqual(0xCAFEF00DCAFEF00Dul, roundTripped.Checksums[30]);
            NAssert.AreEqual(0xFEEDFACEFEEDFACEul, roundTripped.Checksums[60]);

            NAssert.AreEqual(2, roundTripped.Keyframes.Count);
            AssertKeyframesEqual(original.Keyframes[0], roundTripped.Keyframes[0]);
            AssertKeyframesEqual(original.Keyframes[1], roundTripped.Keyframes[1]);

            NAssert.AreEqual(2, roundTripped.Bookmarks.Count);
            AssertBookmarksEqual(original.Bookmarks[0], roundTripped.Bookmarks[0]);
            AssertBookmarksEqual(original.Bookmarks[1], roundTripped.Bookmarks[1]);
        }

        [Test]
        public void RoundTrip_ViaFile()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 1, startFrame: 100, endFrame: 200),
                InitialSnapshot = MakeBytes(seed: 7, length: 64),
                InputQueue = MakeBytes(seed: 8, length: 32),
                Checksums = new IterableDictionary<int, ulong>(),
                Keyframes = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
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
                AssertPayloadsEqual(
                    original.InitialSnapshot,
                    roundTripped.InitialSnapshot,
                    "InitialSnapshot"
                );
                AssertPayloadsEqual(original.InputQueue, roundTripped.InputQueue, "InputQueue");
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
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

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
                Checksums = new IterableDictionary<int, ulong>(),
                Keyframes = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
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
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

            using var stream = new MemoryStream();
            NAssert.Throws<SerializationException>(() => ser.Load(stream));
        }

        [Test]
        public void Load_RejectsMismatchedBundleFormatVersion()
        {
            // Bundles written by a different Trecs build (different
            // BundleFormatVersion) must be rejected with a clear
            // SerializationException — silently misinterpreting an older
            // layout would surface as a confusing downstream parse error or
            // produce a partially-loaded bundle.
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

            // Forge a bundle whose header pretends to be from a future
            // (or past) format version.
            var forged = new RecordingBundle
            {
                Header = new BundleHeader
                {
                    BundleFormatVersion = (byte)(TrecsConstants.CurrentBundleFormatVersion + 1),
                    Version = 1,
                    StartFixedFrame = 0,
                    EndFixedFrame = 1,
                    FixedDeltaTime = 1f / 60f,
                },
                InitialSnapshot = new byte[] { 1 },
                InputQueue = Array.Empty<byte>(),
                Checksums = new IterableDictionary<int, ulong>(),
                Keyframes = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
            };

            using var stream = new MemoryStream();
            ser.Save(forged, stream);
            stream.Position = 0;

            NAssert.Throws<SerializationException>(() => ser.Load(stream));
        }

        [Test]
        public void Load_ThrowsSerializationException_WhenBundleSentinelIsCorrupted()
        {
            // Locks in the BundleSentinel drift path: corrupting the
            // sentinel int that sits just before the payload's trailing
            // EndOfPayloadMarker must surface as a SerializationException
            // whose message names the bundle-sentinel mismatch. Without
            // this test a regression that downgraded the failure mode
            // (e.g. swapping the throw for a debug-only assert, or
            // accidentally widening the comparison) would only show up at
            // runtime against truncated or cross-version bundles in the
            // field — exactly the kind of corruption this guard exists to
            // catch loudly.
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);

            var original = new RecordingBundle
            {
                Header = MakeHeader(version: 1, startFrame: 0, endFrame: 10),
                InitialSnapshot = new byte[] { 1, 2, 3, 4, 5 },
                InputQueue = Array.Empty<byte>(),
                Checksums = new IterableDictionary<int, ulong>(),
                Keyframes = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
            };

            using var stream = new MemoryStream();
            ser.Save(original, stream);
            var bytes = stream.ToArray();

            // BundleSentinel is the last field written into the data section and the wire form has
            // no trailing payload marker, so the sentinel int occupies the final 4 bytes (its
            // little-endian low byte at length-4).
            NAssert.AreEqual(
                (byte)(TrecsConstants.RecordingSentinelValue & 0xFF),
                bytes[bytes.Length - 4],
                "Final 4 bytes should be the BundleSentinel int (little-endian)"
            );

            // Flip a single byte of the sentinel int to break the equality check without changing
            // the payload length, so the section bounds still pass and the bundle sentinel mismatch
            // is what throws.
            bytes[bytes.Length - 4] ^= 0xFF;

            using var corrupted = new MemoryStream(bytes);
            var ex = NAssert.Throws<SerializationException>(() => ser.Load(corrupted));
            StringAssert.Contains("Bundle sentinel mismatch", ex.Message);
        }

        [Test]
        public void Save_RejectsNullRequiredFields()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var ser = new RecordingBundleSerializer(registry);
            using var stream = new MemoryStream();

            // Header missing
            NAssert.Throws<ArgumentNullException>(() =>
                ser.Save(new RecordingBundle { InitialSnapshot = new byte[] { 1 } }, stream)
            );

            // InitialSnapshot empty (rejected with ArgumentException since
            // a bundle without a self-restoring initial payload is replay-useless)
            NAssert.Throws<ArgumentException>(() =>
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
                a.World.Submit();

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

                using var ms = new MemoryStream();
                snapshots.SaveSnapshotToStream(version: 1, stream: ms);
                snapshotBytes = ms.ToArray();
            }

            // Wrap the snapshot bytes in a bundle and round-trip the bundle.
            var registry2 = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry2);
            var bundleSer = new RecordingBundleSerializer(registry2);
            var bundle = new RecordingBundle
            {
                Header = MakeHeader(version: 1, startFrame: 0, endFrame: 0),
                InitialSnapshot = snapshotBytes,
                InputQueue = Array.Empty<byte>(),
                Checksums = new IterableDictionary<int, ulong>(),
                Keyframes = Array.Empty<WorldSnapshot>(),
                Bookmarks = Array.Empty<WorldSnapshot>(),
            };
            var loadedBundle = RoundTripViaMemory(bundleSer, bundle);

            // Now extract the bytes back out and feed them to a fresh world.
            using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
            {
                var a = env.Accessor;
                NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                var worldStateSer = new WorldStateSerializer(env.World);
                var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);

                snapshots.LoadSnapshot(loadedBundle.InitialSnapshot);

                NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));
            }
        }

        [Test]
        public void TrecsRewindBuffer_RoundTripsViaFile()
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
                ulong firstChecksum;

                // ── Record + Save ───────────────────────────────────────────
                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var a = env.Accessor;
                    a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 42 }).AssertComplete();
                    a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 88 }).AssertComplete();
                    a.World.Submit();

                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings
                    {
                        // Pick a small interval so a handful of stepped frames
                        // produce multiple captures.
                        KeyframeIntervalSeconds = 0.05f,
                        Version = 1,
                    };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );
                    recorder.Start();

                    env.StepFixedFrames(20);

                    NAssert.GreaterOrEqual(
                        recorder.Keyframes.Count,
                        2,
                        "Expected at least two keyframes — capture is not running."
                    );

                    snapshotCount = recorder.Keyframes.Count;
                    firstFrame = recorder.Keyframes[0].FixedFrame;
                    lastFrame = recorder.Keyframes[snapshotCount - 1].FixedFrame;
                    firstChecksum = recorder.Checksums[firstFrame];

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
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings { Version = 1 };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );

                    NAssert.IsTrue(recorder.LoadRecordingFromFile(path));

                    // In-memory keyframe list survived the round trip.
                    NAssert.AreEqual(snapshotCount, recorder.Keyframes.Count);
                    NAssert.AreEqual(firstFrame, recorder.Keyframes[0].FixedFrame);
                    NAssert.AreEqual(lastFrame, recorder.Keyframes[snapshotCount - 1].FixedFrame);
                    NAssert.AreEqual(
                        firstChecksum,
                        recorder.Checksums[recorder.Keyframes[0].FixedFrame]
                    );

                    // World state was restored from the initial snapshot.
                    NAssert.AreEqual(2, a.CountEntitiesWithTags(TestTags.Alpha));

                    // Header peek works against the same file.
                    NAssert.IsTrue(TrecsRewindBuffer.TryReadRecordingHeader(path, out var header));
                    NAssert.AreEqual(firstFrame, header.StartFrame);
                    // EndFrame is the recording's save-time frame, which
                    // may exceed the last keyframe when checksum / scrub-cache
                    // cadences fire past the final keyframe — the last keyframe
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
        public void TrecsRewindBuffer_CapturesAndRoundTripsSnapshots()
        {
            // Capture a couple of user snapshots during recording, save, load,
            // and verify the labels + frames + bytes survived. Snapshots are
            // independent of auto-captured keyframes so this also exercises the
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
                    a.World.Submit();

                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings
                    {
                        KeyframeIntervalSeconds = 0.05f,
                        Version = 1,
                    };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );
                    recorder.Start();

                    env.StepFixedFrames(5);
                    NAssert.IsTrue(recorder.CaptureBookmarkAtCurrentFrame("before-the-bug"));
                    firstSnapshotFrame = env.World.FixedFrame;

                    env.StepFixedFrames(8);
                    NAssert.IsTrue(recorder.CaptureBookmarkAtCurrentFrame("after-mitigation"));
                    secondSnapshotFrame = env.World.FixedFrame;

                    NAssert.AreEqual(2, recorder.Bookmarks.Count);
                    NAssert.AreEqual("before-the-bug", recorder.Bookmarks[0].Label);
                    NAssert.AreEqual("after-mitigation", recorder.Bookmarks[1].Label);
                    NAssert.Less(
                        recorder.Bookmarks[0].FixedFrame,
                        recorder.Bookmarks[1].FixedFrame
                    );

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                }

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings { Version = 1 };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );

                    NAssert.IsTrue(recorder.LoadRecordingFromFile(path));

                    NAssert.AreEqual(2, recorder.Bookmarks.Count);
                    NAssert.AreEqual(firstSnapshotFrame, recorder.Bookmarks[0].FixedFrame);
                    NAssert.AreEqual("before-the-bug", recorder.Bookmarks[0].Label);
                    NAssert.AreEqual(secondSnapshotFrame, recorder.Bookmarks[1].FixedFrame);
                    NAssert.AreEqual("after-mitigation", recorder.Bookmarks[1].Label);

                    // Remove a snapshot and verify it's gone.
                    NAssert.IsTrue(recorder.RemoveBookmarkAtFrame(firstSnapshotFrame));
                    NAssert.AreEqual(1, recorder.Bookmarks.Count);
                    NAssert.AreEqual("after-mitigation", recorder.Bookmarks[0].Label);

                    // Removing a frame that no longer has a snapshot is a no-op.
                    NAssert.IsFalse(recorder.RemoveBookmarkAtFrame(firstSnapshotFrame));
                }
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void TrecsRewindBuffer_SnapshotOverwritesAtSameFrame()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var settings = new TrecsRewindBufferSettings
            {
                KeyframeIntervalSeconds = 0.05f,
                Version = 1,
            };
            using var recorder = new TrecsRewindBuffer(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots,
                env.World.CreateOpaqueBlobs()
            );
            recorder.Start();

            env.StepFixedFrames(3);

            NAssert.IsTrue(recorder.CaptureBookmarkAtCurrentFrame("first"));
            NAssert.IsTrue(recorder.CaptureBookmarkAtCurrentFrame("second"));

            // Same frame → second call replaces first, count stays at one.
            NAssert.AreEqual(1, recorder.Bookmarks.Count);
            NAssert.AreEqual("second", recorder.Bookmarks[0].Label);
        }

        [Test]
        public void TrecsRewindBuffer_CapturesKeyframeAtCurrentFrame()
        {
            // Set the auto-keyframe interval high so only the forced first
            // keyframe would land via the cadence path; the manual call adds
            // one more. Verifies the manual API produces a regular keyframe
            // in Keyframes and that the cadence-timer reset suppresses any
            // redundant auto-keyframe immediately after.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var settings = new TrecsRewindBufferSettings
            {
                // Long enough that the only auto-keyframe is the forced first.
                KeyframeIntervalSeconds = 1000f,
                ScrubCacheIntervalSeconds = 1000f,
                Version = 1,
            };
            using var recorder = new TrecsRewindBuffer(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots,
                env.World.CreateOpaqueBlobs()
            );
            recorder.Start();

            env.StepFixedFrames(5);

            // Forced first auto-keyframe lands on the first FixedUpdateCompleted
            // tick regardless of KeyframeIntervalSeconds.
            NAssert.AreEqual(1, recorder.Keyframes.Count);

            NAssert.IsTrue(recorder.CaptureKeyframeAtCurrentFrame());
            var capturedFrame = env.World.FixedFrame;

            NAssert.AreEqual(2, recorder.Keyframes.Count);
            NAssert.AreEqual(capturedFrame, recorder.Keyframes[1].FixedFrame);

            // Step further; the cadence-timer reset means no new auto-keyframe
            // fires (next one would be 1000s away).
            var countAfterManual = recorder.Keyframes.Count;
            env.StepFixedFrames(5);
            NAssert.AreEqual(countAfterManual, recorder.Keyframes.Count);
        }

        [Test]
        public void TrecsRewindBuffer_KeyframeOverwritesAtSameFrame()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var settings = new TrecsRewindBufferSettings
            {
                KeyframeIntervalSeconds = 1000f,
                ScrubCacheIntervalSeconds = 1000f,
                Version = 1,
            };
            using var recorder = new TrecsRewindBuffer(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots,
                env.World.CreateOpaqueBlobs()
            );
            recorder.Start();

            env.StepFixedFrames(3);

            // Keyframes[0] is the forced first auto-keyframe; the two manual
            // captures land at the current frame, the second replacing the
            // first.
            var countBefore = recorder.Keyframes.Count;
            NAssert.IsTrue(recorder.CaptureKeyframeAtCurrentFrame());
            NAssert.IsTrue(recorder.CaptureKeyframeAtCurrentFrame());

            // Manual + replace → only one new entry was added.
            NAssert.AreEqual(countBefore + 1, recorder.Keyframes.Count);
        }

        [Test]
        public void BundleRecorder_CapturesKeyframeAtCurrentFrame()
        {
            // Set the auto-keyframe cadence so high it can't fire during the
            // test, then verify the manual capture API populates Keyframes and
            // the captured keyframe survives the bundle round-trip.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 7 }).AssertComplete();
            env.Accessor.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var settings = new BundleRecorderSettings
            {
                Version = 1,
                KeyframeIntervalSeconds = 1000f,
            };
            using var recorder = new BundleRecorder(env.World, registry, settings, snapshots);
            recorder.Start();

            env.StepFixedFrames(4);
            NAssert.AreEqual(0, recorder.Keyframes.Count);

            NAssert.IsTrue(recorder.CaptureKeyframeAtCurrentFrame());
            var capturedFrame = env.World.FixedFrame;

            NAssert.AreEqual(1, recorder.Keyframes.Count);
            NAssert.AreEqual(capturedFrame, recorder.Keyframes[0].FixedFrame);
            NAssert.Greater(recorder.Keyframes[0].Payload.Length, 0);

            // Double-call at the same frame replaces, doesn't append.
            NAssert.IsTrue(recorder.CaptureKeyframeAtCurrentFrame());
            NAssert.AreEqual(1, recorder.Keyframes.Count);

            env.StepFixedFrames(5);
            var bundle = recorder.Stop();

            NAssert.AreEqual(1, bundle.Keyframes.Count);
            NAssert.AreEqual(capturedFrame, bundle.Keyframes[0].FixedFrame);
        }

        [Test]
        public void TrecsRewindBuffer_ScrubCacheCapturesIndependentOfKeyframes()
        {
            // Configure with sparse keyframes and dense scrub cache. Stepping a
            // handful of frames should produce just the one initial keyframe
            // but multiple scrub-cache entries, observable as
            // LastKeyframeFrame outpacing the last keyframe's frame.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            env.Accessor.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            env.Accessor.World.Submit();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var worldStateSer = new WorldStateSerializer(env.World);
            var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
            var settings = new TrecsRewindBufferSettings
            {
                KeyframeIntervalSeconds = 1000f, // effectively never except the forced first
                ScrubCacheIntervalSeconds = 0.001f, // every frame
                Version = 1,
            };
            using var recorder = new TrecsRewindBuffer(
                env.World,
                worldStateSer,
                registry,
                settings,
                snapshots,
                env.World.CreateOpaqueBlobs()
            );
            recorder.Start();

            env.StepFixedFrames(10);

            // Just one keyframe (the initial), but scrub cache has captured
            // additional frames so the live edge has moved past it.
            NAssert.AreEqual(1, recorder.Keyframes.Count);
            NAssert.Greater(
                recorder.LatestCapturedFrame,
                recorder.Keyframes[0].FixedFrame,
                "Scrub cache should have advanced LastKeyframeFrame past the initial keyframe."
            );
        }

        [Test]
        public void TrecsRewindBuffer_ScrubCacheIsTransientAcrossSaveLoad()
        {
            // Round-trip a recording with a populated scrub cache. After load,
            // only the persisted keyframes should remain — the transient scrub
            // cache is in-memory only and never written.
            var path = Path.Combine(
                Path.GetTempPath(),
                $"trecs_scrubcache_test_{Guid.NewGuid():N}.trec"
            );
            try
            {
                int keyframeCountBeforeSave;
                int lastFrameBeforeSave;

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    env.Accessor.AddEntity(TestTags.Alpha)
                        .Set(new TestInt { Value = 1 })
                        .AssertComplete();
                    env.Accessor.World.Submit();

                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings
                    {
                        KeyframeIntervalSeconds = 1000f,
                        ScrubCacheIntervalSeconds = 0.001f,
                        Version = 1,
                    };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );
                    recorder.Start();

                    env.StepFixedFrames(10);

                    keyframeCountBeforeSave = recorder.Keyframes.Count;
                    lastFrameBeforeSave = recorder.LatestCapturedFrame;
                    NAssert.Greater(
                        lastFrameBeforeSave,
                        recorder.Keyframes[0].FixedFrame,
                        "Test setup: scrub cache should have advanced LastKeyframeFrame."
                    );

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                }

                using (var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha))
                {
                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings { Version = 1 };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );

                    NAssert.IsTrue(recorder.LoadRecordingFromFile(path));

                    NAssert.AreEqual(keyframeCountBeforeSave, recorder.Keyframes.Count);
                    // After load the scrub cache is empty, so LastKeyframeFrame
                    // collapses back onto the last keyframe's frame.
                    NAssert.AreEqual(
                        recorder.Keyframes[recorder.Keyframes.Count - 1].FixedFrame,
                        recorder.LatestCapturedFrame
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
            "TrecsRewindBuffer skips per-frame checksum writes when TRECS_IS_PROFILING is defined, so the round-trip check is a no-op."
        )]
#endif
        [Test]
        public void TrecsRewindBuffer_CapturesPerFrameChecksumsAndRoundTripsThem()
        {
            // Verify the editor recorder populates the bundle's Checksums dict
            // (matching BundleRecorder behaviour) so BundleReplayer's per-frame
            // desync detection works on editor-saved recordings. Without this
            // the saved Checksums dict was empty and desync detection only
            // fired at sparse keyframe frames.
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
                    env.Accessor.World.Submit();

                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var worldStateSer = new WorldStateSerializer(env.World);
                    var snapshots = new SnapshotSerializer(env.World, registry, worldStateSer);
                    var settings = new TrecsRewindBufferSettings
                    {
                        // Sparse keyframes so scrub-cache entries are the dominant
                        // checksum source (checksums are derived from snapshot
                        // capture frames).
                        KeyframeIntervalSeconds = 1000f,
                        ScrubCacheIntervalSeconds = 0.01f,
                        Version = 1,
                    };
                    using var recorder = new TrecsRewindBuffer(
                        env.World,
                        worldStateSer,
                        registry,
                        settings,
                        snapshots,
                        env.World.CreateOpaqueBlobs()
                    );
                    recorder.Start();

                    env.StepFixedFrames(20);

                    NAssert.IsTrue(recorder.SaveRecordingToFile(path));
                }

                // Reopen the file and inspect the dict directly via the
                // serializer, since the recorder doesn't expose the dict.
                {
                    var registry = new SerializerRegistry();
                    DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                    var bundleSer = new RecordingBundleSerializer(registry);
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
                BlobIds = new IterableHashSet<BlobId>(),
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
            NAssert.AreEqual(expected.BlobIds.Count, actual.BlobIds.Count, "BlobIds.Count");
        }

        static void AssertKeyframesEqual(WorldSnapshot expected, WorldSnapshot actual)
        {
            NAssert.AreEqual(expected.FixedFrame, actual.FixedFrame, "Keyframe.FixedFrame");
            NAssert.AreEqual(expected.Kind, actual.Kind, "Keyframe.Kind");
            AssertPayloadsEqual(expected.Payload, actual.Payload, "Keyframe.Payload");
        }

        static void AssertBookmarksEqual(WorldSnapshot expected, WorldSnapshot actual)
        {
            NAssert.AreEqual(expected.FixedFrame, actual.FixedFrame, "Bookmark.FixedFrame");
            NAssert.AreEqual(expected.Kind, actual.Kind, "Bookmark.Kind");
            NAssert.AreEqual(expected.Label, actual.Label, "Bookmark.Label");
            AssertPayloadsEqual(expected.Payload, actual.Payload, "Bookmark.Payload");
        }

        static void AssertPayloadsEqual(
            ReadOnlyMemory<byte> expected,
            ReadOnlyMemory<byte> actual,
            string label
        )
        {
            if (!expected.Span.SequenceEqual(actual.Span))
            {
                NAssert.Fail(
                    $"{label}: byte sequences differ (expected length {expected.Length}, "
                        + $"actual length {actual.Length})"
                );
            }
        }
    }
}
