using System.IO;
using NUnit.Framework;
using Trecs.Serialization;
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
    /// simulation, records checksums every few frames, then replays against a fresh
    /// world (seeded identically and loaded from the same bookmark) and verifies no
    /// desync. This is the canary for the whole recording/playback story — any
    /// non-determinism in the simulation, checksum path, or serialization round-trip
    /// surfaces as <see cref="PlaybackHandler.HasDesynced"/>.
    /// </summary>
    [TestFixture]
    public class RecordingRoundTripTests
    {
        const int FramesToRun = 24;
        const int ChecksumFrameInterval = 3;
        const int Version = 1;
        const ulong RngSeed = 0xFEEDC0FFEEBAD123ul;

        [Test]
        public void RecordThenPlayback_DeterministicSim_NoDesync()
        {
            byte[] recordingBytes;
            byte[] bookmarkBytes;

            // ── Record phase ────────────────────────────────────────────────────
            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = TrecsSerialization.CreateSerializerRegistry();
                var worldStateSer = new WorldStateSerializer(env.World);
                using var bookmarks = new BookmarkSerializer(worldStateSer, registry, env.World);
                using var recorder = new RecordingHandler(worldStateSer, registry, env.World);

                // Save the bookmark BEFORE any recorded ticks so playback has a matching
                // initial state.
                using (var bookmarkStream = new MemoryStream())
                {
                    bookmarks.SaveBookmark(version: Version, stream: bookmarkStream);
                    bookmarkBytes = bookmarkStream.ToArray();
                }

                recorder.StartRecording(
                    version: Version,
                    checksumsEnabled: true,
                    checksumFrameInterval: ChecksumFrameInterval
                );

                env.StepFixedFrames(FramesToRun);

                using var recordingStream = new MemoryStream();
                recorder.EndRecording(recordingStream);
                recordingBytes = recordingStream.ToArray();
            }

            NAssert.Greater(
                recordingBytes.Length,
                0,
                "Recording produced no bytes — recorder/tick loop is broken."
            );

            // ── Playback phase ──────────────────────────────────────────────────
            using (var env = CreateEnv())
            {
                // Note: the playback world is pre-populated with the SAME entities as
                // the recording world and then overwritten by LoadInitialState.
                // LoadBookmark handles entity-count reconciliation, but starting from
                // an identical template is the simplest path.
                SpawnEntities(env);

                var registry = TrecsSerialization.CreateSerializerRegistry();
                var worldStateSer = new WorldStateSerializer(env.World);
                using var bookmarks = new BookmarkSerializer(worldStateSer, registry, env.World);
                using var playback = new PlaybackHandler(
                    worldStateSer,
                    bookmarks,
                    registry,
                    env.World
                );

                using var recordingStream = new MemoryStream(recordingBytes);
                playback.StartPlayback(
                    recordingStream,
                    new PlaybackStartParams { InputsOnly = false, Version = Version }
                );

                using (var bookmarkStream = new MemoryStream(bookmarkBytes))
                {
                    playback.LoadInitialState(bookmarkStream, expectedInitialChecksum: null);
                }

                for (int i = 0; i < FramesToRun; i++)
                {
                    env.StepFixedFrames(1);
                    playback.TickPlayback();

                    NAssert.IsFalse(
                        playback.HasDesynced,
                        $"Playback desynced at frame {i + 1}. "
                            + "Simulation is not deterministic, or the serialization round-trip dropped/mutated state."
                    );
                }

                playback.EndPlayback();
            }
        }

        [Test]
        public void Playback_DesyncsWhenStateIsMutated()
        {
            // Defensive: the "no desync" test above can only fail if mutation ISN'T
            // detected. This sibling test corrupts playback state mid-flight and
            // asserts the handler actually notices — proving the check is live.
            byte[] recordingBytes;
            byte[] bookmarkBytes;

            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = TrecsSerialization.CreateSerializerRegistry();
                var worldStateSer = new WorldStateSerializer(env.World);
                using var bookmarks = new BookmarkSerializer(worldStateSer, registry, env.World);
                using var recorder = new RecordingHandler(worldStateSer, registry, env.World);

                using (var bookmarkStream = new MemoryStream())
                {
                    bookmarks.SaveBookmark(version: Version, stream: bookmarkStream);
                    bookmarkBytes = bookmarkStream.ToArray();
                }

                recorder.StartRecording(
                    version: Version,
                    checksumsEnabled: true,
                    checksumFrameInterval: ChecksumFrameInterval
                );
                env.StepFixedFrames(FramesToRun);
                using var recordingStream = new MemoryStream();
                recorder.EndRecording(recordingStream);
                recordingBytes = recordingStream.ToArray();
            }

            using (var env = CreateEnv())
            {
                SpawnEntities(env);

                var registry = TrecsSerialization.CreateSerializerRegistry();
                var worldStateSer = new WorldStateSerializer(env.World);
                using var bookmarks = new BookmarkSerializer(worldStateSer, registry, env.World);
                using var playback = new PlaybackHandler(
                    worldStateSer,
                    bookmarks,
                    registry,
                    env.World
                );

                using var recordingStream = new MemoryStream(recordingBytes);
                playback.StartPlayback(
                    recordingStream,
                    new PlaybackStartParams { InputsOnly = false, Version = Version }
                );

                using (var bookmarkStream = new MemoryStream(bookmarkBytes))
                {
                    playback.LoadInitialState(bookmarkStream, expectedInitialChecksum: null);
                }

                // Corrupt the state mid-playback — mutate entities so the next checksum
                // cannot possibly match.
                env.StepFixedFrames(1);
                foreach (var ei in env.Accessor.Query().WithTags(Tag<QId1>.Value).EntityIndices())
                {
                    env.Accessor.Component<TestInt>(ei).Write.Value = 999999;
                }
                env.Accessor.SubmitEntities();

                for (int i = 1; i < FramesToRun; i++)
                {
                    env.StepFixedFrames(1);
                    playback.TickPlayback();
                    if (playback.HasDesynced)
                        break;
                }

                NAssert.IsTrue(
                    playback.HasDesynced,
                    "Playback should have detected the injected state corruption as a desync."
                );

                playback.EndPlayback();
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
