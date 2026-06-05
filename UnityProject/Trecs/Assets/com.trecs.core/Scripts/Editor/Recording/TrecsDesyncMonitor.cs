using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Trecs.Internal
{
    /// <summary>
    /// Desync detection + diagnostics for <see cref="TrecsRewindBuffer"/>:
    /// verifies recomputed world-state checksums against the captured ones
    /// while the user walks forward inside the buffer, captures a live
    /// (diverged) snapshot at the first mismatch, and can dump the
    /// recorded-vs-live pair as flat-path text files for offline diffing.
    ///
    /// Owns the lockstep pair (<see cref="DesyncedFrame"/> +
    /// <see cref="LiveSnapshot"/>) that the buffer used to maintain by
    /// convention across seven scattered clear sites — the buffer now just
    /// calls <see cref="Clear"/> whenever it "moves" (Start, Reset, Fork,
    /// JumpToFrame, load-from-file) and <see cref="VerifyAtFrame"/> per
    /// verified tick. Snapshot capture/release/load stay buffer-owned (they
    /// touch the buffer's pools and pin discipline) and come in as callbacks.
    ///
    /// Main-thread only, like its owner.
    /// </summary>
    internal sealed class TrecsDesyncMonitor
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly RecordingEngine _core;
        readonly SnapshotStore _store;
        readonly TrecsRewindBufferSettings _settings;
        readonly SerializationDataPool _serDataPool;

        // Capture the current world state into a pooled SerializationData and
        // pin its opaque blobs (TrecsRewindBuffer.CaptureSnapshotPinned).
        readonly Func<SerializationData, List<BlobPin>> _captureSnapshotPinned;

        // Give a snapshot's resources back (unpin blobs + return its pooled
        // payload/data). The live snapshot is the only WorldSnapshot released
        // outside SnapshotStore's drop sites, so the discipline lives with
        // the buffer that owns the pools.
        readonly Action<WorldSnapshot> _releaseSnapshot;
#if DEBUG
        readonly Action<WorldSnapshot> _loadSnapshot;
        readonly WorldStateSerializer _stateSerializer;
        readonly SerializerRegistry _serializerRegistry;
#endif

        // First frame at which we observed a stored-vs-recomputed checksum
        // mismatch during the current Playback walk. Null while the buffer
        // looks consistent.
        int? _desyncedFrame;

        // Full-state snapshot of the live (diverged) world captured at the
        // moment a desync is detected. Lets the user later compare the
        // recording's snapshot at that frame vs. the diverged live state.
        // Cleared in lockstep with _desyncedFrame (see Clear).
        WorldSnapshot _liveSnapshot;

        public TrecsDesyncMonitor(
            RecordingEngine core,
            SnapshotStore store,
            TrecsRewindBufferSettings settings,
            SerializationDataPool serDataPool,
            Func<SerializationData, List<BlobPin>> captureSnapshotPinned,
            Action<WorldSnapshot> releaseSnapshot,
            Action<WorldSnapshot> loadSnapshot,
            WorldStateSerializer stateSerializer,
            SerializerRegistry serializerRegistry
        )
        {
            _core = core;
            _store = store;
            _settings = settings;
            _serDataPool = serDataPool;
            _captureSnapshotPinned = captureSnapshotPinned;
            _releaseSnapshot = releaseSnapshot;
#if DEBUG
            _loadSnapshot = loadSnapshot;
            _stateSerializer = stateSerializer;
            _serializerRegistry = serializerRegistry;
#endif
        }

        /// <summary>First desynced frame of the current walk, or null while consistent.</summary>
        public int? DesyncedFrame => _desyncedFrame;

        /// <summary>
        /// Full-state snapshot of the live (diverged) world captured the
        /// instant the mismatch was detected; null when no desync.
        /// </summary>
        public WorldSnapshot LiveSnapshot => _liveSnapshot;

        public bool HasDesynced => _desyncedFrame.HasValue;

        /// <summary>
        /// Verify the simulation produced the same world state we captured
        /// at this frame. Called while the user is walking forward inside
        /// the buffer (Playback mode). Sets the desync marker on first
        /// mismatch and stops checking — once desynced the same buffer
        /// can't trust further checksums anyway.
        /// </summary>
        public void VerifyAtFrame(int frame)
        {
            if (_desyncedFrame.HasValue || _core.World.IsDisposed)
            {
                return;
            }
            // _store.Checksums holds entries for snapshot capture frames
            // (keyframes + scrub-cache); absent key = "no checksum at this
            // frame", value 0 = legacy sentinel.
            if (!_store.Checksums.TryGetValue(frame, out var expected) || expected == 0UL)
            {
                return;
            }
            var actual = _core.ComputeChecksum(_settings.Version);
            if (actual != expected)
            {
                // Capture the live (diverged) world state before setting the
                // desync marker so the user can later compare it against the
                // recording's snapshot at this frame.
                var liveData = _serDataPool.Spawn();
                var livePins = _captureSnapshotPinned(liveData);
                _liveSnapshot = new WorldSnapshot
                {
                    FixedFrame = frame,
                    Kind = SnapshotKind.Bookmark,
                    Label = "desync-live",
                    RetainedData = liveData,
                    PinnedBlobs = livePins,
                };

                _desyncedFrame = frame;
                _log.Warning(
                    "Desync at frame {0}: expected checksum {1} but got {2} "
                        + "(simulation re-run from an earlier keyframe produced "
                        + "different state — non-determinism in your code or data).",
                    frame,
                    expected,
                    actual
                );
                EditorApplication.isPaused = true;
            }
        }

        /// <summary>
        /// Release the live snapshot (if any) and clear the desync marker.
        /// Call whenever the buffer "moves" — every scrub gets a fresh
        /// chance to verify.
        /// </summary>
        public void Clear()
        {
            if (_liveSnapshot != null)
            {
                _releaseSnapshot(_liveSnapshot);
                _liveSnapshot = null;
            }
            _desyncedFrame = null;
        }

#if DEBUG
        /// <summary>
        /// Dump both the recorded and live (diverged) world states at the
        /// desynced frame as flat-path text files. Diff the two files to see
        /// exactly which fields diverged.
        /// </summary>
        public (string recordedPath, string livePath)? DumpDiff()
        {
            if (!_desyncedFrame.HasValue)
                return null;

            var frame = _desyncedFrame.Value;
            var dir = Path.Combine(TrecsPaths.LibraryRoot, "desync_diff");
            Directory.CreateDirectory(dir);

            var recordedPath = Path.Combine(dir, $"recorded_frame{frame}.txt");
            var livePath = Path.Combine(dir, $"live_frame{frame}.txt");

            var recordedSnapshot = _store.FindNearestAtOrBefore(frame);
            if (recordedSnapshot == null)
            {
                _log.Warning("No recorded snapshot found at or before frame {0}", frame);
                return null;
            }

            DumpSnapshotToFlatPath(_liveSnapshot, livePath);
            DumpSnapshotToFlatPath(recordedSnapshot, recordedPath);

            _log.Info("Desync diff dumped:\n  recorded: {0}\n  live: {1}", recordedPath, livePath);
            return (recordedPath, livePath);
        }

        void DumpSnapshotToFlatPath(WorldSnapshot snapshot, string path)
        {
            _loadSnapshot(snapshot);

            using var fileStream = File.Create(path);
            using var streamWriter = new StreamWriter(fileStream);

            var writer = new FlatPathSerializationWriter(streamWriter, _serializerRegistry);
            writer.Start(version: _settings.Version, flags: SerializationFlags.DesyncFriendlyHeaps);
            _stateSerializer.SerializeFullState(writer);
            writer.Complete();
        }
#endif
    }
}
