using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Trecs.Internal
{
    /// <summary>
    /// On-disk library for standalone single-frame world-state snapshots
    /// (<c>.snap</c> files). Distinct from <see cref="TrecsRecordingSession"/>,
    /// which orchestrates time-range recordings (<c>.trec</c> files):
    ///
    /// <list type="bullet">
    /// <item><b>Snapshot</b>: a single-frame world state. Useful as a QA
    /// repro fixture or a "revert here later" pin.</item>
    /// <item><b>Recording</b>: a continuous capture you can scrub through.
    /// Saved via <see cref="TrecsRecordingSession.SaveNamedRecording"/>.</item>
    /// </list>
    ///
    /// Capturing a snapshot is free of any recording-session interaction —
    /// it just reads the world's current state. Loading a snapshot,
    /// however, jumps the world's frame and would corrupt an in-progress
    /// recording's continuity, so <see cref="LoadSnapshot"/> stops and
    /// restarts the session around the load when one is active.
    ///
    /// Main-thread only.
    /// </summary>
    internal sealed class TrecsSnapshotLibrary
    {
        /// <summary>File extension for standalone <c>.snap</c> snapshot files.</summary>
        public const string SnapshotExtension = ".snap";

        // Wire version stored in the snapshot file's metadata. Bumped only
        // when the standalone snapshot's binary layout changes
        // incompatibly. Distinct from the world's user-defined schema
        // version (callers compare schema versions themselves).
        const int SnapshotFileVersion = 1;

        static readonly TrecsLog _log = TrecsLog.Default;

        readonly World _world;
        readonly SnapshotSerializer _snapshotSerializer;
        readonly TrecsRecordingSession _session;

        public TrecsSnapshotLibrary(
            World world,
            SnapshotSerializer snapshotSerializer,
            TrecsRecordingSession session
        )
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _snapshotSerializer =
                snapshotSerializer ?? throw new ArgumentNullException(nameof(snapshotSerializer));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// Capture the world's current state to <c>{TrecsPaths.Snapshots}/{name}.snap</c>.
        /// Safe to call while a recording session is active — only reads
        /// world state.
        /// </summary>
        public bool SaveSnapshot(string name)
        {
            TrecsDebugAssert.That(
                !string.IsNullOrWhiteSpace(name),
                "Snapshot name must not be empty"
            );
            if (_world.IsDisposed)
            {
                _log.Warning("Cannot save snapshot: world is disposed");
                return false;
            }
            var path = GetSnapshotPath(name);
            try
            {
                Directory.CreateDirectory(GetSnapshotsDirectory());
                _snapshotSerializer.SaveSnapshot(
                    SnapshotFileVersion,
                    path,
                    includeTypeChecks: true
                );
                _log.Info("Saved snapshot '{0}' to {1}", name, path);
                TrecsRecordingSession.NotifySavesChanged();
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("Failed to save snapshot '{0}': {1}", name, e.Message);
                return false;
            }
        }

        /// <summary>
        /// Restore the world to the state stored in <c>{name}.snap</c>.
        /// If a recording session is active when this is called, the
        /// session is stopped before the load and restarted fresh from the
        /// loaded frame afterwards (so the user doesn't land in LIVE limbo).
        /// </summary>
        public bool LoadSnapshot(string name)
        {
            TrecsDebugAssert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetSnapshotPath(name);
            if (!File.Exists(path))
            {
                _log.Warning("Snapshot '{0}' does not exist at {1}", name, path);
                return false;
            }
            // Loading a snapshot restores world state to a different frame,
            // which would corrupt the existing recording's continuity. Stop
            // it first; if the user was recording, restart fresh from the
            // loaded frame afterwards so they don't land in LIVE limbo (the
            // Replay window's "New" button can't recover from a stopped
            // recorder — Reset early-exits when state is Idle).
            var wasRecording = _session.AutoRecorder.IsRecording;
            _session.StopAutoRecording();
            try
            {
                _snapshotSerializer.LoadSnapshot(path);
                // LoadSnapshot only restores component state; the input
                // queue still has the abandoned timeline's inputs at >=
                // the loaded frame. The next tick's input-phase AddInput
                // would trip an "Input already exists" assert. Same pattern
                // as the rewind buffer's DropAbandonedTimelineInputs after
                // JumpToFrame.
                _session.ClearFutureInputsAtCurrentFrame();
                if (wasRecording)
                {
                    _session.StartAutoRecording();
                }
                _session.PollModeChanged();
                _log.Info("Loaded snapshot '{0}'", name);
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("Failed to load snapshot '{0}': {1}", name, e.Message);
                return false;
            }
        }

        public bool DeleteSnapshot(string name)
        {
            TrecsDebugAssert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetSnapshotPath(name);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            _log.Info("Deleted snapshot '{0}'", name);
            TrecsRecordingSession.NotifySavesChanged();
            return true;
        }

        public static bool RenameSnapshot(string oldName, string newName) =>
            RenameInDirectory(GetSnapshotsDirectory(), oldName, newName, SnapshotExtension);

        public static IReadOnlyList<string> GetSavedSnapshotNames() =>
            ListSavesIn(GetSnapshotsDirectory(), SnapshotExtension);

        public static string GetSnapshotsDirectory() => TrecsPaths.Snapshots;

        public static string GetSnapshotPath(string name) =>
            Path.Combine(GetSnapshotsDirectory(), name + SnapshotExtension);

        // ---- Internal helpers ---------------------------------------------

        static bool RenameInDirectory(
            string directory,
            string oldName,
            string newName,
            string extension
        )
        {
            TrecsDebugAssert.That(!string.IsNullOrWhiteSpace(oldName));
            TrecsDebugAssert.That(!string.IsNullOrWhiteSpace(newName));
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return false;
            }
            var src = Path.Combine(directory, oldName + extension);
            var dst = Path.Combine(directory, newName + extension);
            if (!File.Exists(src) || File.Exists(dst))
            {
                return false;
            }
            File.Move(src, dst);
            _log.Info("Renamed '{0}' → '{1}' in {2}", oldName, newName, directory);
            TrecsRecordingSession.NotifySavesChanged();
            return true;
        }

        static IReadOnlyList<string> ListSavesIn(string dir, string extension)
        {
            if (!Directory.Exists(dir))
            {
                return Array.Empty<string>();
            }
            return Directory
                .EnumerateFiles(dir, "*" + extension)
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
