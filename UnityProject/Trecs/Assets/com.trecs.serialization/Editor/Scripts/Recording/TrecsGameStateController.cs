using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Trecs.Internal;
using UnityEngine;

namespace Trecs.Serialization
{
    /// <summary>
    /// Coordinates the game-state-serialization features that touch the World's
    /// input queue and component state: auto-recording, named snapshots/recordings,
    /// scrub-back playback. Editor tooling and runtime callers go through this
    /// controller rather than driving <see cref="TrecsAutoRecorder"/> directly,
    /// so transitions stay valid.
    ///
    /// The "mode" is two orthogonal axes:
    ///   * Source: <see cref="GameStateMode"/> — Idle, Recording, or Playback
    ///     (Recording vs Playback is derived from whether the recorder is at
    ///     the live edge of its buffer).
    ///   * Run: <see cref="IsPaused"/> — straight pass-through of the
    ///     SystemRunner's FixedIsPaused flag.
    /// </summary>
    public class TrecsGameStateController : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(TrecsGameStateController));

        readonly World _world;
        readonly TrecsAutoRecorder _autoRecorder;
        readonly SnapshotSerializer _snapshotSerializer;

        WorldAccessor _accessor;
        GameStateMode _lastBroadcastMode = GameStateMode.Idle;
        string _lastBroadcastLoadedRecordingName;
        IDisposable _frameSubscription;

        public TrecsGameStateController(
            World world,
            TrecsAutoRecorder autoRecorder,
            SnapshotSerializer snapshotSerializer
        )
        {
            _world = world;
            _autoRecorder = autoRecorder;
            _snapshotSerializer = snapshotSerializer;
        }

        public World World => _world;
        public TrecsAutoRecorder AutoRecorder => _autoRecorder;

        /// <summary>
        /// Source axis. Derived from recorder state on every read so it always
        /// matches reality, including silent transitions when the simulation
        /// advances past a divergence point and Playback collapses back to
        /// Recording.
        /// </summary>
        public GameStateMode CurrentMode
        {
            get
            {
                if (!_autoRecorder.IsRecording)
                {
                    return GameStateMode.Idle;
                }
                // Loaded recordings are always Playback — they don't capture
                // forward, only replay. Auto-recordings flip Recording↔Playback
                // based on live-edge state.
                if (_autoRecorder.IsLoadedRecording)
                {
                    return GameStateMode.Playback;
                }
                return _autoRecorder.IsAtLiveEdge
                    ? GameStateMode.Recording
                    : GameStateMode.Playback;
            }
        }

        /// <summary>True iff the SystemRunner's fixed phase is paused.</summary>
        public bool IsPaused
        {
            get
            {
                if (!TryGetRunner(out var runner))
                {
                    return false;
                }
                return runner.FixedIsPaused;
            }
        }

        /// <summary>
        /// Fires whenever <see cref="CurrentMode"/> changes — including silent
        /// Playback↔Recording transitions detected by polling. UI code that
        /// already polls per-tick can ignore this; consumers wanting an event
        /// should call <see cref="PollModeChanged"/> on a regular cadence.
        /// </summary>
        public event Action<GameStateMode> ModeChanged;

        /// <summary>
        /// Name (without extension) of the on-disk file backing the
        /// recorder's in-memory buffer, or null when the buffer is fresh,
        /// post-Reset, or post-Fork. Drives the Player window's header
        /// label and the "Save vs Save As" branching, plus the loaded-row
        /// highlight in the Saves window.
        /// </summary>
        public string LoadedRecordingName
        {
            get
            {
                var path = _autoRecorder.LoadedRecordingPath;
                return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
            }
        }

        /// <summary>
        /// Fires whenever <see cref="LoadedRecordingName"/> changes — load,
        /// save, reset, fork, fresh-start, world dispose. Detected by the
        /// same polling loop as <see cref="ModeChanged"/>.
        /// </summary>
        public event Action LoadedRecordingChanged;

        /// <summary>
        /// Fires whenever the on-disk save library changes — a recording or
        /// snapshot was saved, deleted, or renamed. Lets the Saves window
        /// (and any other library views) refresh without polling. Static so
        /// any controller's edits notify all observers.
        /// </summary>
        public static event Action SavesChanged;

        /// <summary>
        /// Fire <see cref="SavesChanged"/>. Public-static so the editor
        /// windows can notify subscribers after fallback paths that don't
        /// route through the controller (e.g. deleting a file from the
        /// Saves window when no live world exists).
        /// </summary>
        public static void NotifySavesChanged() => SavesChanged?.Invoke();

        public void Initialize()
        {
            _accessor = _world.CreateAccessor("TrecsGameStateController");
            _lastBroadcastMode = CurrentMode;
            // Poll on every fixed frame so auto-promotions (Playback →
            // Recording when the simulation walks past the buffer tail) flip
            // the input-system enable state immediately, even if no UI is
            // open to call PollModeChanged for us.
            _frameSubscription = _accessor
                .GetSystemRunner()
                .FixedFrameChangeEvent.Subscribe(_ => PollModeChanged());
            TrecsGameStateRegistry.Register(this);
        }

        public void Dispose()
        {
            _frameSubscription?.Dispose();
            _frameSubscription = null;
            if (_autoRecorder.IsRecording)
            {
                _autoRecorder.Stop();
            }
            // Stop() above flips the recorder out of Recording → mode is Idle.
            // Re-enable input systems if the last broadcast left them off
            // (e.g. the user closed Unity while in Playback).
            if (_lastBroadcastMode == GameStateMode.Playback)
            {
                SetInputSystemsEnabled(true);
                _lastBroadcastMode = GameStateMode.Idle;
            }
            TrecsGameStateRegistry.Unregister(this);
            _accessor = null;
        }

        /// <summary>
        /// Re-evaluate <see cref="CurrentMode"/> and fire <see cref="ModeChanged"/>
        /// if it changed since the last poll. Cheap; intended to be called from
        /// the UI's existing refresh loop so consumers that subscribe to
        /// ModeChanged see Playback↔Recording transitions.
        /// </summary>
        public void PollModeChanged()
        {
            var current = CurrentMode;
            if (current != _lastBroadcastMode)
            {
                var previous = _lastBroadcastMode;
                _lastBroadcastMode = current;
                ApplyInputSystemsForMode(previous, current);
                ModeChanged?.Invoke(current);
            }
            var currentName = LoadedRecordingName;
            if (
                !string.Equals(
                    currentName,
                    _lastBroadcastLoadedRecordingName,
                    StringComparison.Ordinal
                )
            )
            {
                _lastBroadcastLoadedRecordingName = currentName;
                LoadedRecordingChanged?.Invoke();
            }
        }

        // Toggles SystemPhase.Input systems based on the mode transition.
        // Recording / Idle keep input systems live; Playback silences them so
        // the recorded inputs in EntityInputQueue (held by the recorder's
        // history locker) drive the world unaltered. We only flip on actual
        // transitions out of / into Playback so we don't hammer the systems
        // on every poll or override unrelated callers that might also toggle
        // these systems.
        void ApplyInputSystemsForMode(GameStateMode previous, GameStateMode current)
        {
            if (current == GameStateMode.Playback)
            {
                SetInputSystemsEnabled(false);
            }
            else if (previous == GameStateMode.Playback)
            {
                SetInputSystemsEnabled(true);
            }
        }

        void SetInputSystemsEnabled(bool enable)
        {
            if (_accessor == null || _world.IsDisposed)
            {
                return;
            }
            for (int i = 0; i < _world.SystemCount; i++)
            {
                if (_world.GetSystemMetadata(i).Phase != SystemPhase.Input)
                {
                    continue;
                }
                if (_world.IsSystemEnabled(i, EnableChannel.Playback) != enable)
                {
                    _world.SetSystemEnabled(i, EnableChannel.Playback, enable);
                }
            }
        }

        public void StartAutoRecording()
        {
            if (_autoRecorder.IsRecording)
            {
                return;
            }
            _autoRecorder.Start();
            PollModeChanged();
        }

        public void StopAutoRecording()
        {
            if (!_autoRecorder.IsRecording)
            {
                return;
            }
            _autoRecorder.Stop();
            PollModeChanged();
        }

        public bool JumpToFrame(int targetFrame)
        {
            if (!_autoRecorder.IsRecording)
            {
                _log.Warning("Cannot jump to frame: not recording");
                return false;
            }
            var ok = _autoRecorder.JumpToFrame(targetFrame);
            PollModeChanged();
            return ok;
        }

        public bool JumpToPreviousSnapshot()
        {
            if (!_autoRecorder.IsRecording)
            {
                return false;
            }
            var ok = _autoRecorder.JumpToPreviousSnapshot();
            PollModeChanged();
            return ok;
        }

        public bool JumpToNextSnapshot()
        {
            if (!_autoRecorder.IsRecording)
            {
                return false;
            }
            var ok = _autoRecorder.JumpToNextSnapshot();
            PollModeChanged();
            return ok;
        }

        /// <summary>
        /// Truncate any snapshots past the current frame and resume Recording
        /// from here. Use when the user has scrubbed into Playback and wants
        /// to commit the buffer at this point (dropping the "future" the
        /// recorder is holding speculatively). After this call the recorder
        /// is at the live edge and the world is unpaused.
        /// </summary>
        public bool ForkAtCurrentFrame()
        {
            if (!_autoRecorder.IsRecording)
            {
                _log.Warning("Cannot fork: not recording");
                return false;
            }
            if (!_autoRecorder.ForkAtCurrentFrame())
            {
                return false;
            }
            if (TryGetRunner(out var runner))
            {
                runner.FixedIsPaused = false;
            }
            PollModeChanged();
            return true;
        }

        /// <summary>Set the SystemRunner's fixed pause flag.</summary>
        public void SetPaused(bool paused)
        {
            if (TryGetRunner(out var runner))
            {
                runner.FixedIsPaused = paused;
            }
        }

        /// <summary>Step one fixed frame; pauses first if not already paused.</summary>
        public void StepFrame()
        {
            if (!TryGetRunner(out var runner))
            {
                return;
            }
            if (!runner.FixedIsPaused)
            {
                runner.FixedIsPaused = true;
            }
            runner.StepFrame();
        }

        bool TryGetRunner(out SystemRunner runner)
        {
            runner = null;
            if (_accessor == null || _world.IsDisposed)
            {
                return false;
            }
            runner = _accessor.GetSystemRunner();
            return runner != null;
        }

        /// <summary>
        /// Persist the current auto-recording (the in-memory snapshot list) to
        /// disk under <paramref name="name"/>. Saved recordings live under
        /// <c>&lt;project&gt;/svkj_temp/trecs_debug/recordings/</c>.
        /// </summary>
        public bool SaveNamedRecording(string name)
        {
            Assert.That(!string.IsNullOrWhiteSpace(name), "Recording name must not be empty");
            if (!_autoRecorder.IsRecording)
            {
                _log.Warning("Cannot save recording: not recording");
                return false;
            }
            var path = GetRecordingPath(name);
            if (!_autoRecorder.SaveRecordingToFile(path))
            {
                return false;
            }
            _log.Info("Saved recording '{}' to {}", name, path);
            // Save sets the recorder's LoadedRecordingPath so a subsequent
            // "Save" overwrites the same slot. Poll so subscribers learn
            // about the new name without waiting for the next fixed tick.
            PollModeChanged();
            NotifySavesChanged();
            return true;
        }

        /// <summary>
        /// Replace the current auto-recording in memory with a previously-saved
        /// one and pause the world at its earliest snapshot so the user can
        /// scrub through it.
        /// </summary>
        public bool LoadNamedRecording(string name)
        {
            Assert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetRecordingPath(name);
            if (!File.Exists(path))
            {
                _log.Warning("Recording '{}' does not exist at {}", name, path);
                return false;
            }

            // Stop existing recording so the deserialize doesn't fire change
            // events back into a live recorder.
            StopAutoRecording();

            if (!_autoRecorder.LoadRecordingFromFile(path))
            {
                return false;
            }

            // Recorder set _isRecording=true internally; broadcast the new mode.
            PollModeChanged();
            _log.Info("Loaded recording '{}'", name);
            return true;
        }

        public bool DeleteNamedRecording(string name)
        {
            Assert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetRecordingPath(name);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            // If the buffer was backed by this file, detach so the Player
            // header doesn't keep showing a stale name.
            _autoRecorder.ClearLoadedPathIfMatches(path);
            PollModeChanged();
            _log.Info("Deleted recording '{}'", name);
            NotifySavesChanged();
            return true;
        }

        /// <summary>
        /// Rename a saved recording file from <paramref name="oldName"/> to
        /// <paramref name="newName"/>. Returns false on any failure (missing
        /// source, existing destination, identical names).
        /// </summary>
        public static bool RenameNamedRecording(string oldName, string newName)
        {
            return RenameInDirectory(GetRecordingsDirectory(), oldName, newName);
        }

        static bool RenameInDirectory(string directory, string oldName, string newName)
        {
            Assert.That(!string.IsNullOrWhiteSpace(oldName));
            Assert.That(!string.IsNullOrWhiteSpace(newName));
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return false;
            }
            var src = Path.Combine(directory, oldName + ".bin");
            var dst = Path.Combine(directory, newName + ".bin");
            if (!File.Exists(src) || File.Exists(dst))
            {
                return false;
            }
            File.Move(src, dst);
            _log.Info("Renamed '{}' → '{}' in {}", oldName, newName, directory);
            NotifySavesChanged();
            return true;
        }

        public static IReadOnlyList<string> GetSavedRecordingNames()
        {
            return ListBinFilesIn(GetRecordingsDirectory());
        }

        public static string GetRecordingsDirectory()
        {
            return GetDebugSubdirectory("recordings");
        }

        public static string GetRecordingPath(string name)
        {
            return Path.Combine(GetRecordingsDirectory(), name + ".bin");
        }

        // ---- Snapshots (standalone single-frame snapshots) ----
        //
        // Snapshots are loose save-states distinct from recordings: a single
        // captured world state, no input timeline. They serve as QA repro
        // fixtures or "revert here later" pins. Storage uses the same
        // SnapshotSerializer format as the recording's per-frame snapshots
        // so loading a snapshot is just LoadSnapshot(stream). Capturing
        // works in any mode (including while auto-recording) without
        // disturbing the live recording — we only read the world's current
        // state; we never write back into the recorder's snapshot list.
        // Loading jumps the world's frame, which would corrupt an active
        // recording's continuity, so we stop the recorder around the load
        // and start a fresh one rooted at the snapshot's frame if the user
        // was recording before.

        const int SnapshotFileVersion = 1;

        public bool SaveSnapshot(string name)
        {
            Assert.That(!string.IsNullOrWhiteSpace(name), "Snapshot name must not be empty");
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
                _log.Info("Saved snapshot '{}' to {}", name, path);
                NotifySavesChanged();
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("Failed to save snapshot '{}': {}", name, e.Message);
                return false;
            }
        }

        public bool LoadSnapshot(string name)
        {
            Assert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetSnapshotPath(name);
            if (!File.Exists(path))
            {
                _log.Warning("Snapshot '{}' does not exist at {}", name, path);
                return false;
            }
            // Loading a snapshot restores world state to a different frame, which
            // would corrupt the existing recording's continuity. Stop it first;
            // if the user was recording, restart fresh from the loaded frame
            // afterwards so they don't land in LIVE limbo (the Replay window's
            // "New" button can't recover from a stopped recorder — Reset
            // early-exits when !IsRecording).
            var wasRecording = _autoRecorder.IsRecording;
            StopAutoRecording();
            try
            {
                _snapshotSerializer.LoadSnapshot(path);
                // LoadSnapshot only restores component state; the input queue
                // still has the abandoned timeline's inputs at >= the loaded
                // frame. The next tick's input-phase AddInput would trip an
                // "Input already exists" assert. Same pattern as the auto-
                // recorder's DropAbandonedTimelineInputs after JumpToFrame.
                if (_accessor != null)
                {
                    _accessor
                        .GetEntityInputQueue()
                        .ClearFutureInputsAfterOrAt(_accessor.FixedFrame);
                }
                if (wasRecording)
                {
                    StartAutoRecording();
                }
                PollModeChanged();
                _log.Info("Loaded snapshot '{}'", name);
                return true;
            }
            catch (Exception e)
            {
                _log.Warning("Failed to load snapshot '{}': {}", name, e.Message);
                return false;
            }
        }

        public bool DeleteSnapshot(string name)
        {
            Assert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetSnapshotPath(name);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            _log.Info("Deleted snapshot '{}'", name);
            NotifySavesChanged();
            return true;
        }

        public static bool RenameSnapshot(string oldName, string newName)
        {
            return RenameInDirectory(GetSnapshotsDirectory(), oldName, newName);
        }

        public static IReadOnlyList<string> GetSavedSnapshotNames()
        {
            return ListBinFilesIn(GetSnapshotsDirectory());
        }

        public static string GetSnapshotsDirectory()
        {
            return GetDebugSubdirectory("snapshots");
        }

        public static string GetSnapshotPath(string name)
        {
            return Path.Combine(GetSnapshotsDirectory(), name + ".bin");
        }

        /// <summary>
        /// Discard all current auto-recording snapshots and start fresh from
        /// the world's current frame. Useful when the user wants to shape a
        /// recording before saving it (e.g. play a bit, reset, then capture
        /// the interesting part).
        /// </summary>
        public void ResetAutoRecording()
        {
            if (!_autoRecorder.IsRecording)
            {
                _log.Warning("Cannot reset: not recording");
                return;
            }
            _autoRecorder.Reset();
            PollModeChanged();
        }

        static string GetDebugSubdirectory(string name)
        {
            // Application.dataPath is <project>/Assets — go up one to project root.
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "svkj_temp", "trecs_debug", name)
            );
        }

        static IReadOnlyList<string> ListBinFilesIn(string dir)
        {
            if (!Directory.Exists(dir))
            {
                return Array.Empty<string>();
            }
            return Directory
                .EnumerateFiles(dir, "*.bin")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
