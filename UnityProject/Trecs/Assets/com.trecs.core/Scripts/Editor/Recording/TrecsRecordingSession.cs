using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Trecs.Internal
{
    /// <summary>
    /// Coordinates the game-state-serialization features that touch the World's
    /// input queue and component state: auto-recording, named snapshots/recordings,
    /// scrub-back playback. Editor tooling and runtime callers go through this
    /// controller rather than driving <see cref="TrecsRewindBuffer"/> directly,
    /// so transitions stay valid.
    ///
    /// The "mode" is two orthogonal axes:
    ///   * Source: <see cref="GameStateMode"/> — Idle, Recording, or Playback
    ///     (Recording vs Playback is derived from whether the recorder is at
    ///     the live edge of its buffer).
    ///   * Run: <see cref="IsPaused"/> — straight pass-through of the
    ///     SystemRunner's FixedIsPaused flag.
    /// </summary>
    internal class TrecsRecordingSession : IDisposable
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        /// <summary>File extension for full <see cref="RecordingBundle"/> files
        /// (input timeline + periodic auto-anchors + user snapshots). Distinct from the
        /// snapshot extension so a shared "Saves" directory listing tells the
        /// two file kinds apart at a glance.</summary>
        public const string RecordingExtension = ".trec";

        readonly World _world;
        readonly TrecsRewindBuffer _autoRecorder;
        readonly WorldAccessor _accessor;
        readonly IDisposable _frameSubscription;

        GameStateMode _lastBroadcastMode = GameStateMode.Idle;
        string _lastBroadcastLoadedRecordingName;

        public TrecsRecordingSession(World world, TrecsRewindBuffer autoRecorder)
        {
            _world = world;
            _autoRecorder = autoRecorder;
            _accessor = _world.CreateAccessor(AccessorRole.Unrestricted, "TrecsRecordingSession");
            _lastBroadcastMode = CurrentMode;
            // Poll on every fixed frame so auto-promotions (Playback →
            // Recording when the simulation walks past the buffer tail) flip
            // the input-system enable state immediately, even if no UI is
            // open to call PollModeChanged for us.
            _frameSubscription = _accessor
                .GetSystemRunner()
                .FixedFrameChangeEvent.Subscribe(_ => PollModeChanged());
            TrecsRecordingSessionRegistry.Register(this);
        }

        public World World => _world;
        public TrecsRewindBuffer AutoRecorder => _autoRecorder;

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

        public void Dispose()
        {
            _frameSubscription.Dispose();
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
            TrecsRecordingSessionRegistry.Unregister(this);
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
            var systems = _world.GetSystems();
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i].Phase != SystemPhase.Input)
                {
                    continue;
                }
                if (_accessor.IsSystemEnabled(i, EnableChannel.Playback) != enable)
                {
                    _accessor.SetSystemEnabled(i, EnableChannel.Playback, enable);
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

        public bool JumpToPreviousAnchor()
        {
            if (!_autoRecorder.IsRecording)
            {
                return false;
            }
            var ok = _autoRecorder.JumpToPreviousAnchor();
            PollModeChanged();
            return ok;
        }

        public bool JumpToNextAnchor()
        {
            if (!_autoRecorder.IsRecording)
            {
                return false;
            }
            var ok = _autoRecorder.JumpToNextAnchor();
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
        public void StepFixedFrame()
        {
            if (!TryGetRunner(out var runner))
            {
                return;
            }
            if (!runner.FixedIsPaused)
            {
                runner.FixedIsPaused = true;
            }
            runner.StepFixedFrame();
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
        /// <see cref="TrecsPaths.Recordings"/>.
        /// </summary>
        public bool SaveNamedRecording(string name)
        {
            TrecsDebugAssert.That(
                !string.IsNullOrWhiteSpace(name),
                "Recording name must not be empty"
            );
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
            _log.Info("Saved recording '{0}' to {1}", name, path);
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
            TrecsDebugAssert.That(!string.IsNullOrWhiteSpace(name));
            var path = GetRecordingPath(name);
            if (!File.Exists(path))
            {
                _log.Warning("Recording '{0}' does not exist at {1}", name, path);
                return false;
            }

            // Stop existing recording so the deserialize doesn't fire change
            // events back into a live recorder.
            StopAutoRecording();

            if (!_autoRecorder.LoadRecordingFromFile(path))
            {
                return false;
            }

            // Recorder set _state = LoadedPlayback internally; broadcast the new mode.
            PollModeChanged();
            _log.Info("Loaded recording '{0}'", name);
            return true;
        }

        public bool DeleteNamedRecording(string name)
        {
            TrecsDebugAssert.That(!string.IsNullOrWhiteSpace(name));
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
            _log.Info("Deleted recording '{0}'", name);
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
            return RenameInDirectory(
                GetRecordingsDirectory(),
                oldName,
                newName,
                RecordingExtension
            );
        }

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
            NotifySavesChanged();
            return true;
        }

        public static IReadOnlyList<string> GetSavedRecordingNames()
        {
            return ListSavesIn(GetRecordingsDirectory(), RecordingExtension);
        }

        public static string GetRecordingsDirectory()
        {
            return TrecsPaths.Recordings;
        }

        public static string GetRecordingPath(string name)
        {
            return Path.Combine(GetRecordingsDirectory(), name + RecordingExtension);
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

        /// <summary>
        /// Clear inputs from the world's <c>EntityInputQueue</c> at or after
        /// the current fixed frame. Used by callers that have just restored
        /// world state out-of-band (e.g.
        /// <see cref="TrecsSnapshotLibrary.LoadSnapshot"/>): without this,
        /// the next tick's input-phase <c>AddInput</c> would assert against
        /// leftover queued inputs from the abandoned timeline.
        /// </summary>
        public void ClearFutureInputsAtCurrentFrame()
        {
            if (_accessor == null || _world.IsDisposed)
            {
                return;
            }
            _accessor.GetEntityInputQueue().ClearFutureInputsAfterOrAt(_accessor.FixedFrame);
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
