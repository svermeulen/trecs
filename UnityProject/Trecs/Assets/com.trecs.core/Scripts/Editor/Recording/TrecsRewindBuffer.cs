using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Trecs.Collections;
using UnityEditor;
using UnityEngine;

namespace Trecs.Internal
{
    /// <summary>
    /// Periodically captures full-state snapshots of a Trecs <see cref="World"/>
    /// while running. Snapshots are kept in memory only — no disk I/O. Designed
    /// to be enabled on demand by editor tooling (see <c>TrecsPlayerWindow</c>).
    /// </summary>
    internal class TrecsRewindBuffer : IDisposable, IInputHistoryLocker
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        // Lifecycle / IO primitives (accessor + checksum calculator + reusable
        // buffers + queue serialization + locker registration) live on this
        // shared collaborator that the runtime recorder (BundleRecorder) also
        // owns — see RecorderEngine docs for the split rationale.
        readonly RecorderEngine _core;
        readonly TrecsRewindBufferSettings _settings;
        readonly RecordingBundleSerializer _bundleSerializer;
#if DEBUG
        readonly IWorldStateSerializer _stateSerializer;
        readonly SerializerRegistry _serializerRegistry;
#endif

        // In-memory store for anchors / bookmarks / scrub cache / per-frame
        // checksums. Owns payload-pool return discipline and byte accounting
        // so the recorder doesn't have to.
        readonly SnapshotStore _store;
        readonly SnapshotPayloadPool _pool;

        IDisposable _frameSubscription;

        // Authoritative state of the recorder's playhead + buffer kind.
        // Replaces what used to be an implicit machine across _isRecording
        // + _isLoadedRecording + _scrubbedFrame +
        // _fastForwardTarget; see BufferState below for transitions.
        BufferState _state = BufferState.Idle;
        int _startFrame;

        // Frame of the most recent persisted-anchor capture, used by the
        // anchor-cadence timer in CaptureSnapshotIfDue. Null means "no prior
        // capture", which forces an immediate capture on the next frame —
        // used after Start/Reset/Trim so we don't have to wait an entire
        // cadence interval before the first anchor.
        int? _lastAnchorFrame;

        // Same idea for the transient scrub cache, on a separate (denser)
        // cadence.
        int? _lastScrubCacheFrame;

        // Frame the playhead is paused at after a scrub. Valid in
        // BufferState.Scrubbed and BufferState.LoadedPlayback. In Scrubbed
        // (auto-recording), snapshots past this frame are *tentatively* stale
        // — the user can keep scrubbing freely without losing them. They get
        // truncated only on the first user-driven (non-fast-forward) fixed-
        // update tick that advances the simulation past this point. In
        // LoadedPlayback this only marks "where you last scrubbed to"; the
        // loaded buffer's "future" is never truncated. Null in LiveCapture
        // and FastForwarding; null in LoadedPlayback once the playhead has
        // walked past the most recent scrub.
        int? _scrubbedFrame;

        // Target frame of an active fast-forward. Valid only when
        // _state == BufferState.FastForwarding. While set, FF inner-loop
        // ticks are skipped for capture/truncation purposes (we don't want
        // to capture mid-FF snapshots, and we don't want the truncation
        // logic to mistake FF progression for user-driven progression).
        int? _fastForwardTarget;

        // State to transition into when FastForwarding reaches its target.
        // Either Scrubbed (auto-recording buffer) or LoadedPlayback (loaded
        // from .trec). Valid only when _state == BufferState.FastForwarding.
        BufferState _postFastForwardState;

        // Tracks whether we are currently registered with the EntityInputQueue
        // as an IInputHistoryLocker. Add/remove operations are gated through
        // EnsureLockerRegistered so we never double-register or double-remove.
        bool _lockerRegistered;

        // First frame at which we observed a stored-vs-recomputed checksum
        // mismatch during the current Playback walk. Null while the buffer
        // looks consistent. Cleared on Start, Reset, Fork, JumpToFrame
        // (every scrub gets a fresh chance to verify), and load-from-file.
        int? _desyncedFrame;

        // Full-state snapshot of the live (diverged) world captured at the
        // moment a desync is detected. Lets the user later compare the
        // recording's snapshot at that frame vs. the diverged live state.
        // Cleared wherever _desyncedFrame is cleared.
        WorldSnapshot _desyncLiveSnapshot;

        public TrecsRewindBuffer(
            World world,
            IWorldStateSerializer stateSerializer,
            SerializerRegistry serializerRegistry,
            TrecsRewindBufferSettings settings,
            SnapshotSerializer snapshotSerializer,
            SnapshotPayloadPool pool
        )
        {
            _settings = settings;
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _core = new RecorderEngine(
                world,
                serializerRegistry,
                snapshotSerializer,
                accessorLabel: nameof(TrecsRewindBuffer)
            );
            _store = new SnapshotStore(pool);
            _bundleSerializer = new RecordingBundleSerializer(serializerRegistry);
#if DEBUG
            _stateSerializer = stateSerializer;
            _serializerRegistry = serializerRegistry;
#endif
        }

        public World World => _core.World;

        /// <summary>Authoritative playhead + buffer-kind state.</summary>
        public BufferState State => _state;

        /// <summary>
        /// True iff the recorder is doing anything (any state other than
        /// <see cref="BufferState.Idle"/>). Compatibility alias —
        /// new call sites should prefer <see cref="State"/>.
        /// </summary>
        public bool IsRecording => _state != BufferState.Idle;

        public int StartFrame => _startFrame;

        /// <summary>
        /// Rightmost captured frame across persisted anchors, bookmarks,
        /// and the transient scrub cache. Drives the player UI's slider end
        /// position and the "at live edge" check.
        /// </summary>
        public int LatestCapturedFrame => _store.LatestCapturedFrame ?? _startFrame;

        public IReadOnlyList<WorldSnapshot> Anchors => _store.Anchors;

        /// <summary>
        /// User-placed labelled bookmarks. Independent of auto-captured
        /// <see cref="Anchors"/>: deleting a bookmark never affects an anchor
        /// at the same frame, and bookmarks are not subject to the
        /// auto-recorder's capacity caps.
        /// </summary>
        public IReadOnlyList<WorldSnapshot> Bookmarks => _store.Bookmarks;

        /// <summary>
        /// World-state checksums at snapshot capture frames. Single source of
        /// truth for anchor and bookmark capture frames — verify-on-replay and
        /// desync detection key on this dict.
        /// </summary>
        public IterableDictionary<int, ulong> Checksums => _store.Checksums;

        public long TotalBytes => _store.TotalAnchorBytes;

        /// <summary>
        /// True iff the playhead is at the recorder's live edge —
        /// <see cref="BufferState.LiveCapture"/>. Drives the Recording vs
        /// Playback distinction in the controller.
        /// </summary>
        public bool IsAtLiveEdge => _state == BufferState.LiveCapture;

        /// <summary>
        /// Interval (in simulated seconds) between persisted-anchor captures.
        /// Larger values save file size; smaller values reduce the maximum
        /// resimulation needed during desync recovery.
        /// </summary>
        public float AnchorIntervalSeconds
        {
            get => _settings.AnchorIntervalSeconds;
            set => _settings.AnchorIntervalSeconds = Mathf.Max(0.001f, value);
        }

        /// <summary>
        /// Interval (in simulated seconds) between transient scrub-cache
        /// captures. The scrub cache is in-memory only and makes recent-frame
        /// scrub-back instant. Smaller values = snappier scrubbing.
        /// </summary>
        public float ScrubCacheIntervalSeconds
        {
            get => _settings.ScrubCacheIntervalSeconds;
            set => _settings.ScrubCacheIntervalSeconds = Mathf.Max(0.001f, value);
        }

        /// <summary>
        /// Always false. Retained for editor-window compatibility; the
        /// drop-oldest capacity model never pauses the simulation.
        /// </summary>
        public bool IsPausedByCapacity => false;

        /// <summary>
        /// True iff the in-memory buffer came from a loaded recording file —
        /// i.e. the playhead is in <see cref="BufferState.LoadedPlayback"/>,
        /// or in <see cref="BufferState.FastForwarding"/> heading there.
        /// Loaded buffers preserve their trailing snapshots (the recording's
        /// future) when simulation advances, and auto-pause when the loaded
        /// buffer is exhausted. Cleared by Start/Reset/Fork.
        /// </summary>
        public bool IsLoadedRecording =>
            _state == BufferState.LoadedPlayback
            || (
                _state == BufferState.FastForwarding
                && _postFastForwardState == BufferState.LoadedPlayback
            );

        /// <summary>
        /// Absolute path of the on-disk file backing the in-memory buffer,
        /// or null if the buffer has not been saved or loaded (fresh
        /// auto-recording, post-Reset, post-Fork). Updated by both
        /// <see cref="LoadRecordingFromFile"/> and
        /// <see cref="SaveRecordingToFile"/> so that "Save" can overwrite
        /// the same slot after a "Save As", and that loading via the Saves
        /// window propagates the name to the Player surface.
        /// </summary>
        public string LoadedRecordingPath { get; private set; }

        /// <summary>
        /// Detach from the on-disk file backing the in-memory buffer iff
        /// it matches <paramref name="filePath"/>. Called by the controller
        /// after deleting a recording from disk so the Player no longer
        /// pretends the buffer is "Saved" against a file that no longer
        /// exists.
        /// </summary>
        public void ClearLoadedPathIfMatches(string filePath)
        {
            if (string.Equals(LoadedRecordingPath, filePath, StringComparison.Ordinal))
            {
                LoadedRecordingPath = null;
            }
        }

        /// <summary>Configured maximum persisted-anchor count (0 = unbounded).</summary>
        public int MaxAnchorCount
        {
            get => _settings.MaxAnchorCount;
            set => _settings.MaxAnchorCount = Mathf.Max(0, value);
        }

        /// <summary>Configured maximum scrub-cache byte budget (0 = unbounded).</summary>
        public long MaxScrubCacheBytes
        {
            get => _settings.MaxScrubCacheBytes;
            set => _settings.MaxScrubCacheBytes = Math.Max(0, value);
        }

        // Session-local: when true, reaching the loaded recording's tail
        // jumps back to the start frame instead of pausing. Reset to false
        // any time we transition out of "loaded recording" state (Start /
        // LoadRecording / ForkAtCurrentFrame / Reset) so the user has to opt
        // back in for each recording session — Loop is a transient "I'm
        // watching this on repeat" mode, not a persisted preference.
        bool _loopPlayback;

        /// <summary>
        /// Session-local toggle. When true, reaching the last snapshot of a
        /// loaded recording rewinds to the start frame and continues playing
        /// instead of pausing.
        /// </summary>
        public bool LoopPlayback
        {
            get => _loopPlayback;
            set => _loopPlayback = value;
        }

        /// <summary>
        /// Fractional fill of the tighter of the two caps (anchor count and
        /// scrub-cache bytes), in [0, 1]. Returns 0 when both caps are
        /// unbounded — callers can hide the meter in that case.
        /// </summary>
        public float CapacityFraction
        {
            get
            {
                var byAnchorCount =
                    _settings.MaxAnchorCount > 0
                        ? _store.Anchors.Count / (float)_settings.MaxAnchorCount
                        : 0f;
                var byScrubBytes =
                    _settings.MaxScrubCacheBytes > 0
                        ? _store.ScrubCacheBytes / (float)_settings.MaxScrubCacheBytes
                        : 0f;
                return Mathf.Clamp01(Mathf.Max(byAnchorCount, byScrubBytes));
            }
        }

        /// <summary>
        /// Frame at which the user last scrubbed back, or null if at the live
        /// edge. Exposed for UI purposes (e.g. showing where the divergence
        /// will commit if simulation advances past it).
        /// </summary>
        public int? PendingDivergenceFrame => _scrubbedFrame;

        /// <summary>
        /// Set to the frame where a desync was first detected during the
        /// current Playback walk — i.e. the simulation re-ran from an earlier
        /// snapshot and produced a state whose checksum did not match the
        /// originally captured one. Null when the buffer is consistent (or
        /// no checksums are available, see <see cref="ChecksumsAvailable"/>).
        /// Cleared whenever the buffer "moves" — Start, Reset, Fork,
        /// JumpToFrame, LoadRecordingFromFile.
        /// </summary>
        public int? DesyncedFrame => _desyncedFrame;

        /// <summary>
        /// Full-state snapshot of the live (diverged) world captured at the
        /// instant a checksum mismatch was detected. Null when no desync has
        /// occurred. Paired with the recording's snapshot at
        /// <see cref="DesyncedFrame"/> to let external tooling diff the two
        /// states.
        /// </summary>
        public WorldSnapshot DesyncLiveSnapshot => _desyncLiveSnapshot;

        /// <summary>True iff a desync has been detected.</summary>
        public bool HasDesynced => _desyncedFrame.HasValue;

        /// <summary>
        /// IInputHistoryLocker implementation. Returns the latest frame we
        /// allow the EntityInputQueue to prune at-or-before. Inputs strictly
        /// past the earliest still-scrubbable frame are kept so scrub-back +
        /// Play can replay the original inputs verbatim.
        ///
        /// Returns null when there's nothing to lock — recorder not running,
        /// no frames captured yet, or world disposed — so the queue's default
        /// cleanup (currentFrame - 1) applies.
        /// </summary>
        public int? MaxClearFrame
        {
            get
            {
                if (_state == BufferState.Idle)
                {
                    return null;
                }
                // Use the earliest scrubbable frame across anchors,
                // bookmarks, and the scrub cache — replaying from any of
                // those needs inputs from that frame onward. Falls back to
                // _startFrame pre-first-capture.
                var earliest = _store.EarliestCapturedFrame ?? _startFrame;
                return earliest - 1;
            }
        }

        public void Dispose()
        {
            Stop();
            _store.Dispose();
            _core.Dispose();
            _bundleSerializer.Dispose();
        }

        public void Start()
        {
            if (_state != BufferState.Idle)
            {
                return;
            }

            _store.Clear();
            _startFrame = _core.Accessor.FixedFrame;
            // Force the first FixedUpdateCompleted tick to capture an anchor
            // immediately. We don't capture here in Start() because the
            // activator may invoke us during Layer.Initialize, before
            // downstream serializers (e.g. Orca's LuaStateSerializer) have
            // finished their own init.
            _lastAnchorFrame = null;
            _lastScrubCacheFrame = null;
            _scrubbedFrame = null;
            _fastForwardTarget = null;
            _loopPlayback = false;
            ClearDesyncLiveSnapshot();
            _desyncedFrame = null;
            _state = BufferState.LiveCapture;
            // Fresh recording — discard any prior backing-file name so a
            // subsequent "Save" prompts for a new name rather than
            // overwriting the previously-loaded slot.
            LoadedRecordingPath = null;

            // Lock input history at-or-after _startFrame so the queue's
            // periodic cleanup doesn't prune frames the user might want to
            // scrub back to. Without this, scrub-back + Play replays no
            // inputs because they were already discarded as "old".
            EnsureLockerRegistered(true);

            _frameSubscription = _core.SubscribeFixedUpdateCompleted(OnFixedFrameChange);

            _log.Debug("Auto recording started at fixed frame {0}", _startFrame);
        }

        public void Stop()
        {
            if (_state == BufferState.Idle)
            {
                return;
            }

            _frameSubscription?.Dispose();
            _frameSubscription = null;
            _state = BufferState.Idle;
            _fastForwardTarget = null;
            _scrubbedFrame = null;
            EnsureLockerRegistered(false);

            _log.Debug(
                "Auto recording stopped — captured {0} anchors ({1} bytes total)",
                _store.Anchors.Count,
                _store.TotalAnchorBytes
            );
        }

        /// <summary>
        /// Restore the world state to <paramref name="targetFrame"/> by loading
        /// the latest snapshot whose frame is <c>&lt;= targetFrame</c>, and (if
        /// needed) fast-forwarding the simulation up to <paramref name="targetFrame"/>.
        /// Snapshots past <paramref name="targetFrame"/> are kept (tentatively
        /// stale) so the user can keep scrubbing forward and back while paused;
        /// they get truncated only when the simulation actually progresses past
        /// the load point with live (user-driven) input. The world is left
        /// fixed-paused at the target so the user can inspect.
        /// Returns false if there is no snapshot at or before <paramref name="targetFrame"/>.
        /// </summary>
        public bool JumpToFrame(int targetFrame)
        {
            if (_state == BufferState.Idle)
            {
                _log.Warning("JumpToFrame called while not recording");
                return false;
            }
            if (_core.World.IsDisposed)
            {
                return false;
            }

            var nearest = _store.FindNearestAtOrBefore(targetFrame);
            if (nearest == null)
            {
                if (_store.IsEmpty)
                {
                    _log.Warning("No anchors recorded yet — cannot jump");
                    return false;
                }
                // Target precedes everything in the buffer. Snap up to the
                // earliest scrubbable frame across anchors / scrub cache /
                // bookmarks.
                nearest = _store.FindEarliest();
                targetFrame = nearest.Value.frame;
            }

            var (anchorFrame, anchorPayload) = nearest.Value;
            var runner = _core.Accessor.GetSystemRunner();

            // Detach from frame events so we don't capture a "snapshot" of the
            // half-restored state during the load itself.
            _frameSubscription?.Dispose();
            _frameSubscription = null;

            try
            {
                _core.SnapshotSerializer.LoadSnapshot(anchorPayload);

                // NOTE: we deliberately do NOT clear future inputs here.
                // Those inputs ARE the recording's content — clearing them
                // would mean Play (or fast-forward through the buffer)
                // re-walks the timeline with no input data, so the player
                // wouldn't move. The locker prevents the queue's normal
                // cleanup from pruning them. They're discarded only on an
                // explicit Fork or Reset, which truly abandon the timeline.

                // Buffer kind is preserved across the jump: a loaded buffer
                // stays loaded, an auto-recording buffer stays auto. So
                // post-jump state depends on what we were doing before.
                var postJumpRest = IsLoadedRecording
                    ? BufferState.LoadedPlayback
                    : BufferState.Scrubbed;

                if (targetFrame > _core.Accessor.FixedFrame)
                {
                    // FastForward requires !FixedIsPaused; SystemRunner
                    // re-pauses automatically once the target frame is reached.
                    // The scrubbed frame is set to the FF target when the FF
                    // completes (in OnFixedFrameChange).
                    _state = BufferState.FastForwarding;
                    _fastForwardTarget = targetFrame;
                    _postFastForwardState = postJumpRest;
                    _scrubbedFrame = null;
                    runner.FixedIsPaused = false;
                    runner.FastForwardTargetFrame = targetFrame;
                }
                else
                {
                    // Already at the loaded frame; that frame is now the user's
                    // scrub position. Snapshots past it are tentatively stale.
                    _state = postJumpRest;
                    _fastForwardTarget = null;
                    _scrubbedFrame = _core.Accessor.FixedFrame;
                    runner.FixedIsPaused = true;
                }

                // Each new scrub gets a fresh chance to verify checksums.
                // The previous desync (if any) was tied to the prior walk;
                // clear it so the user can scrub past the suspected frame
                // and watch for it again, or jump elsewhere and observe.
                ClearDesyncLiveSnapshot();
                _desyncedFrame = null;

                // Re-subscribe so further frames continue to snapshot normally.
                _frameSubscription = _core.SubscribeFixedUpdateCompleted(OnFixedFrameChange);
            }
            catch (Exception e)
            {
                // World state may be partially loaded — keep recording running
                // would silently capture corrupt snapshots. Stop cleanly so the
                // user sees auto-recording as inactive and can decide to
                // restart.
                _log.Error("JumpToFrame to snapshot @ frame {0} failed: {1}", anchorFrame, e);
                _state = BufferState.Idle;
                _fastForwardTarget = null;
                _scrubbedFrame = null;
                return false;
            }

            _log.Debug("Jumped to frame {0} via snapshot at frame {1}", targetFrame, anchorFrame);
            return true;
        }

        /// <summary>
        /// Lightweight inspection of a saved recording's header — frame span and
        /// tick rate, without loading snapshots, inputs, or checksums.
        /// Cheap enough to call per-file when listing the saves library.
        /// Returns false if the file is missing or the binary payload is invalid.
        /// </summary>
        public static bool TryReadRecordingHeader(string filePath, out RecordingHeader header)
        {
            header = default;
            if (!File.Exists(filePath))
                return false;
            try
            {
                // Caller (saves library) caches results by mtime so this only
                // runs on first read or when the file changes; constructing a
                // fresh registry+serializer per call keeps the API static.
                var registry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
                using var serializer = new RecordingBundleSerializer(registry);
                var bundleHeader = serializer.PeekHeader(filePath);
                header = new RecordingHeader(
                    bundleHeader.StartFixedFrame,
                    bundleHeader.EndFixedFrame,
                    bundleHeader.FixedDeltaTime
                );
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Persist the current in-memory snapshot list, plus the live
        /// EntityInputQueue covering its frame range, to <paramref name="filePath"/>.
        /// The recorder must currently be running and have at least one
        /// snapshot.
        /// </summary>
        public bool SaveRecordingToFile(string filePath)
        {
            if (_state == BufferState.Idle)
            {
                _log.Warning("SaveRecordingToFile called while not recording");
                return false;
            }
            if (_store.Anchors.Count == 0)
            {
                _log.Warning("SaveRecordingToFile: no anchors to save");
                return false;
            }

            var fixedDeltaTime = _core.Accessor.GetSystemRunner().FixedDeltaTime;
            var blobs = new IterableHashSet<BlobId>();
            _core.World.BlobCache.GetAllActiveBlobIds(blobs);

            // EntityInputQueue is serialized via its own SerializationBuffer
            // envelope; the resulting bytes are an opaque payload inside the
            // outer bundle. Same shape BundleReplayer expects when reading
            // the queue out on load.
            var queueBytes = _core.SerializeEntityInputQueue(_settings.Version);

            // The in-memory anchor list is treated as the initial snapshot
            // (first entry) plus a sequence of trailing anchors (the rest).
            // User-placed bookmarks live in their own list and round-trip
            // independently. The per-frame checksums dict is populated under
            // its own cadence in CaptureSnapshotIfDue and persisted into the
            // bundle so BundleReplayer can detect desyncs close to where they
            // happen rather than only at sparse anchor frames.
            var storeAnchors = _store.Anchors;
            var initial = storeAnchors[0];
            var trailingAnchors = new List<WorldSnapshot>(storeAnchors.Count - 1);
            for (int i = 1; i < storeAnchors.Count; i++)
            {
                trailingAnchors.Add(storeAnchors[i]);
            }

            var bundle = new RecordingBundle
            {
                Header = new BundleHeader
                {
                    Version = _settings.Version,
                    StartFixedFrame = initial.FixedFrame,
                    // Current frame, not the last anchor's frame: per-frame
                    // checksums, scrub-cache entries, and bookmarks can extend
                    // past the last anchor when their cadences differ. Matches
                    // BundleRecorder.Stop, which uses _core.Accessor.FixedFrame.
                    EndFixedFrame = _core.Accessor.FixedFrame,
                    FixedDeltaTime = fixedDeltaTime,
                    BlobIds = blobs,
                },
                // The anchor's payload is already a ReadOnlyMemory<byte> sized
                // to the exact valid range even when the backing pooled byte[]
                // is oversized, so we can hand it through directly without a
                // copy.
                InitialSnapshot = initial.Payload,
                InputQueue = queueBytes,
                Checksums = _store.CopyChecksumsForBundle(),
                Anchors = trailingAnchors,
                Bookmarks = _store.Bookmarks.ToArray(),
            };

            _bundleSerializer.Save(bundle, filePath);

            // Mark this file as the buffer's backing slot so a follow-up
            // "Save" overwrites it without reprompting (and so the Player
            // header shows the name).
            LoadedRecordingPath = filePath;

            _log.Debug(
                "Saved recording: {0} anchors, {1} bookmarks, {2} blob refs, {3} bytes input queue",
                _store.Anchors.Count,
                _store.Bookmarks.Count,
                blobs.Count,
                queueBytes.Length
            );
            return true;
        }

        /// <summary>
        /// Replace the in-memory snapshot list with one read from
        /// <paramref name="filePath"/>, restore world state to the earliest
        /// loaded snapshot, and leave the world fixed-paused there. Re-attaches
        /// the FixedUpdateCompleted subscription so further snapshots will be
        /// captured when the user steps or unpauses.
        /// </summary>
        public bool LoadRecordingFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                _log.Warning("Recording file does not exist: {0}", filePath);
                return false;
            }

            RecordingBundle bundle;
            try
            {
                bundle = _bundleSerializer.Load(filePath);
            }
            catch (Exception e)
            {
                _log.Error("Failed to read recording from {0}: {1}", filePath, e);
                return false;
            }

            if (bundle.InitialSnapshot.IsEmpty)
            {
                _log.Warning("Loaded recording has no initial snapshot");
                return false;
            }

            if (bundle.Header.Version != _settings.Version)
            {
                _log.Warning(
                    "Recording schema version {0} does not match current {1} — "
                        + "load may fail or the simulation may desync",
                    bundle.Header.Version,
                    _settings.Version
                );
            }

            var currentFixedDeltaTime = _core.Accessor.GetSystemRunner().FixedDeltaTime;
            if (!Mathf.Approximately(bundle.Header.FixedDeltaTime, currentFixedDeltaTime))
            {
                _log.Warning(
                    "Recording fixed delta time {0} differs from current {1} — input replay may desync",
                    bundle.Header.FixedDeltaTime,
                    currentFixedDeltaTime
                );
            }

            // Reconstruct the in-memory anchor list: initial snapshot at the
            // head, then trailing anchors. Persisted anchors and the transient
            // scrub cache are kept separate (anchors survive Save/Load; the
            // scrub cache is rebuilt per session). Initial snapshot's
            // checksum lands in _store.Checksums below alongside the trailing
            // per-frame entries.
            var loadedAnchors = new List<WorldSnapshot>(bundle.Anchors.Count + 1)
            {
                new WorldSnapshot
                {
                    FixedFrame = bundle.Header.StartFixedFrame,
                    Kind = SnapshotKind.Anchor,
                    Label = "",
                    Payload = bundle.InitialSnapshot,
                },
            };
            foreach (var anchor in bundle.Anchors)
            {
                loadedAnchors.Add(anchor);
            }

            // Detach the subscription for the duration of the load.
            _frameSubscription?.Dispose();
            _frameSubscription = null;

            try
            {
                // Restore world state to the earliest loaded snapshot.
                var earliest = loadedAnchors[0];
                _core.SnapshotSerializer.LoadSnapshot(earliest.Payload);

                // Wipe the live queue and replace it with the recording's
                // serialized inputs. ClearAllInputs (vs. ClearFutureInputsAfterOrAt)
                // is correct here: we're switching timelines wholesale, and any
                // pre-loaded-frame inputs on the live timeline don't apply.
                _core.Accessor.GetEntityInputQueue().ClearAllInputs();
                if (!bundle.InputQueue.IsEmpty)
                {
                    _core.DeserializeEntityInputQueue(bundle.InputQueue.Span);
                }

                // Replace in-memory state with the loaded recording. Clear
                // pool-returns every existing payload; then re-populate the
                // store via its Append* APIs to keep byte accounting honest.
                _store.Clear();
                foreach (var a in loadedAnchors)
                {
                    _store.AppendAnchor(a);
                }
                foreach (var b in bundle.Bookmarks)
                {
                    _store.InsertOrReplaceBookmarkAt(b);
                }
                // Preserve loaded per-frame checksums so a subsequent re-save
                // (post-fork or post-trim) doesn't lose desync-detection
                // coverage. Post-Load Checksums is contractually non-null
                // (RecordingBundleSerializer.Load always populates it; older
                // format bundles are rejected via BundleFormatVersion).
                foreach (var (frame, checksum) in bundle.Checksums)
                {
                    _store.SetChecksum(frame, checksum);
                }
                _startFrame = bundle.Header.StartFixedFrame;
                _lastAnchorFrame = _store.Anchors[_store.Anchors.Count - 1].FixedFrame;
                _lastScrubCacheFrame = null;
                // Cadence rebases at the load point — the bundle's own
                // Checksums dict already covers the playback range; live
                // capture (if any) starts checksumming again from here.
                _scrubbedFrame = earliest.FixedFrame;
                _fastForwardTarget = null;
                _state = BufferState.LoadedPlayback;
                _loopPlayback = false;
                ClearDesyncLiveSnapshot();
                _desyncedFrame = null;
                LoadedRecordingPath = filePath;

                // Hold input history for the loaded buffer's frame range so
                // the deserialized inputs aren't pruned by the queue's
                // normal cleanup before the user gets a chance to scrub.
                EnsureLockerRegistered(true);

                // Pause at the loaded frame so the user can scrub through.
                _core.Accessor.GetSystemRunner().FixedIsPaused = true;

                _frameSubscription = _core.SubscribeFixedUpdateCompleted(OnFixedFrameChange);
            }
            catch (Exception e)
            {
                // Same recovery as JumpToFrame: world state may be partially
                // loaded; stop recording cleanly so the user can decide.
                _log.Error("LoadRecordingFromFile from {0} failed: {1}", filePath, e);
                _store.Clear();
                _state = BufferState.Idle;
                _fastForwardTarget = null;
                _scrubbedFrame = null;
                ClearDesyncLiveSnapshot();
                _desyncedFrame = null;
                LoadedRecordingPath = null;
                EnsureLockerRegistered(false);
                // Force the reused queue buffer back to Idle so a subsequent
                // SaveRecordingToFile / LoadRecordingFromFile can safely use it
                // again. No-op if the queue deserialize succeeded; required if
                // any step inside this try threw while the buffer was in
                // Reading state.
                _core.ResetQueueBufferForErrorRecovery();
                return false;
            }

            var loadedAnchorList = _store.Anchors;
            _log.Debug(
                "Loaded recording with {0} anchors (frames {1} .. {2})",
                loadedAnchorList.Count,
                loadedAnchorList[0].FixedFrame,
                loadedAnchorList[loadedAnchorList.Count - 1].FixedFrame
            );
            return true;
        }

        /// <summary>
        /// Truncate any snapshots past the world's current frame and clear
        /// pending divergence so the recorder is at the live edge again.
        /// Used by the controller's "Fork &amp; resume" gesture: the user has
        /// scrubbed into Playback, decides to commit at this point, and wants
        /// new live input to extend the buffer from here. The recorder must
        /// currently be running and have at least one snapshot at or before
        /// the current frame.
        /// </summary>
        public bool ForkAtCurrentFrame()
        {
            if (_state == BufferState.Idle || _core.World.IsDisposed)
            {
                return false;
            }
            var current = _core.Accessor.FixedFrame;
            if (_store.FindAnchorIndexAtOrBefore(current) < 0)
            {
                _log.Warning(
                    "ForkAtCurrentFrame: no anchor at or before frame {0} — cannot fork",
                    current
                );
                return false;
            }
            _store.TrimAnchorsAfter(current);
            _store.TrimScrubCacheAfter(current);
            _store.TrimBookmarksAfter(current);
            _store.TrimChecksumsAfter(current);
            _lastAnchorFrame =
                _store.Anchors.Count > 0
                    ? _store.Anchors[_store.Anchors.Count - 1].FixedFrame
                    : (int?)null;
            _lastScrubCacheFrame =
                _store.ScrubCache.Count > 0
                    ? _store.ScrubCache[_store.ScrubCache.Count - 1].FixedFrame
                    : _lastAnchorFrame;
            // Past-fork checksums may have been trimmed.
            DropAbandonedTimelineInputs();
            _scrubbedFrame = null;
            _fastForwardTarget = null;
            _state = BufferState.LiveCapture;
            _loopPlayback = false;
            ClearDesyncLiveSnapshot();
            _desyncedFrame = null;
            // Keep LoadedRecordingPath attached so a subsequent plain Save
            // overwrites the original file — fork is the "edit this recording
            // from frame N onward" gesture. Save As remains the path for
            // saving under a different name.
            _log.Debug("Forked recording at frame {0}; trailing anchors dropped", current);
            return true;
        }

        /// <summary>
        /// Drop snapshots whose frame is strictly less than <paramref name="frame"/>,
        /// preserving the closest snapshot at-or-before <paramref name="frame"/> (so
        /// <see cref="JumpToFrame"/> at the trim point still works). The new earliest
        /// snapshot becomes the recording's start frame; queued inputs prior to it
        /// are pruned in lockstep. Returns the count of dropped snapshots (0 if no
        /// trim happened — recorder not running, no candidates to drop, or the trim
        /// point is at-or-before the current earliest snapshot).
        /// </summary>
        public int TrimRecordingBefore(int frame)
        {
            if (_state == BufferState.Idle)
            {
                return 0;
            }
            var bytesBefore = _store.TotalAnchorBytes;
            var dropped = _store.TrimAnchorsBefore(frame);
            if (dropped == 0)
            {
                return 0;
            }
            _startFrame = _store.Anchors[0].FixedFrame;
            _store.TrimBookmarksBefore(_startFrame);
            _store.TrimScrubCacheBefore(_startFrame);
            _store.TrimChecksumsBefore(_startFrame);
            if (!_core.World.IsDisposed)
            {
                _core.Accessor.GetEntityInputQueue().ClearInputsBeforeOrAt(_startFrame - 1);
            }
            _log.Debug(
                "Trimmed {0} anchors before frame {1} ({2} bytes dropped)",
                dropped,
                frame,
                bytesBefore - _store.TotalAnchorBytes
            );
            return dropped;
        }

        /// <summary>
        /// Drop snapshots whose frame is strictly greater than <paramref name="frame"/>.
        /// Other recorder state (divergence, loaded-recording flag, desync) is
        /// intentionally preserved — this is a pure data trim, distinct from
        /// <see cref="ForkAtCurrentFrame"/> which also commits the current scrub
        /// frame as the new live edge. Returns the count of dropped snapshots.
        /// </summary>
        public int TrimRecordingAfter(int frame)
        {
            if (_state == BufferState.Idle)
            {
                return 0;
            }
            var bytesBefore = _store.TotalAnchorBytes;
            var dropped = _store.TrimAnchorsAfter(frame);
            _store.TrimScrubCacheAfter(frame);
            _store.TrimBookmarksAfter(frame);
            _store.TrimChecksumsAfter(frame);
            _lastAnchorFrame =
                _store.Anchors.Count > 0
                    ? _store.Anchors[_store.Anchors.Count - 1].FixedFrame
                    : (int?)null;
            _lastScrubCacheFrame =
                _store.ScrubCache.Count > 0
                    ? _store.ScrubCache[_store.ScrubCache.Count - 1].FixedFrame
                    : _lastAnchorFrame;
            if (dropped == 0)
            {
                return 0;
            }
            if (!_core.World.IsDisposed)
            {
                // JumpToFrame deliberately preserves future inputs because they
                // ARE the recording's content — but here we're explicitly
                // discarding the post-frame timeline, so drop those inputs in
                // lockstep. Otherwise playing forward past `frame` would
                // re-consume the orphaned inputs and reproduce the same
                // gameplay the user just trimmed away.
                _core.Accessor.GetEntityInputQueue().ClearFutureInputsAfterOrAt(frame + 1);
            }
            _log.Debug(
                "Trimmed {0} anchors after frame {1} ({2} bytes dropped)",
                dropped,
                frame,
                bytesBefore - _store.TotalAnchorBytes
            );
            return dropped;
        }

        /// <summary>
        /// Discard all in-memory snapshot history and start a fresh recording
        /// from the world's current frame. The recorder must currently be
        /// running.
        /// </summary>
        public void Reset()
        {
            if (_state == BufferState.Idle)
            {
                _log.Warning("Reset called while not recording");
                return;
            }
            _store.Clear();
            _startFrame = _core.Accessor.FixedFrame;
            _lastAnchorFrame = null;
            _lastScrubCacheFrame = null;
            _scrubbedFrame = null;
            _fastForwardTarget = null;
            _state = BufferState.LiveCapture;
            _loopPlayback = false;
            ClearDesyncLiveSnapshot();
            _desyncedFrame = null;
            LoadedRecordingPath = null;
            DropAbandonedTimelineInputs();
            _log.Debug("Auto recording reset at frame {0}", _startFrame);
        }

        /// <summary>
        /// Capture a labelled full-state bookmark at the world's current
        /// fixed frame and add it to <see cref="Bookmarks"/>. Replaces any
        /// existing bookmark at the same frame. Bookmarks survive Save/Load
        /// and are independent of auto-captured anchors.
        /// </summary>
        /// <param name="label">Display label for the recorder UI. Empty
        /// string is allowed; null is rejected.</param>
        /// <returns>True if the bookmark was captured.</returns>
        public bool CaptureBookmarkAtCurrentFrame(string label)
        {
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }
            if (_state == BufferState.Idle)
            {
                _log.Warning("CaptureBookmarkAtCurrentFrame called while not recording");
                return false;
            }
            if (_core.World.IsDisposed)
            {
                return false;
            }

            _core.SnapshotSerializer.SaveSnapshot(
                _settings.Version,
                out var payload,
                out var checksum,
                includeTypeChecks: true
            );
            var bookmark = new WorldSnapshot
            {
                FixedFrame = _core.Accessor.FixedFrame,
                Kind = SnapshotKind.Bookmark,
                Label = label,
                Payload = payload,
            };
            _store.InsertOrReplaceBookmarkAt(bookmark);
            _store.SetChecksum(bookmark.FixedFrame, checksum);
            _log.Debug("Captured bookmark at frame {0} (label='{1}')", bookmark.FixedFrame, label);
            return true;
        }

        /// <summary>
        /// Capture an unlabeled full-state anchor at the world's current
        /// fixed frame, outside the normal
        /// <see cref="AnchorIntervalSeconds"/> cadence. Symmetric with
        /// <see cref="CaptureBookmarkAtCurrentFrame"/> but without a label —
        /// the captured anchor lives in <see cref="Anchors"/> alongside
        /// auto-captured anchors and is subject to the same
        /// <see cref="MaxAnchorCount"/> cap. Replaces any existing anchor at
        /// the same frame, and resets the auto-anchor cadence timer so the
        /// next auto-capture fires a full interval from here rather than
        /// redundantly moments later.
        /// </summary>
        /// <returns>True iff the anchor was captured.</returns>
        public bool CaptureAnchorAtCurrentFrame()
        {
            if (_state == BufferState.Idle)
            {
                _log.Warning("CaptureAnchorAtCurrentFrame called while not recording");
                return false;
            }
            if (_core.World.IsDisposed)
            {
                return false;
            }

            var metadata = _core.SnapshotSerializer.SaveSnapshot(
                _settings.Version,
                out var payload,
                out var checksum,
                includeTypeChecks: true
            );
            var anchor = new WorldSnapshot
            {
                FixedFrame = metadata.FixedFrame,
                Kind = SnapshotKind.Anchor,
                Label = "",
                Payload = payload,
            };
            _store.SetChecksum(anchor.FixedFrame, checksum);

            // Anchor cadence: a manual capture satisfies the "we have a
            // recovery point near here" need just as well as an auto one, so
            // pretend we just made an auto-capture. Same treatment for the
            // scrub-cache cadence — anchors double as scrub-cache entries
            // during navigation, so we reset that tracker too to avoid an
            // immediate redundant scrub-cache capture on the next tick.
            _lastAnchorFrame = metadata.FixedFrame;
            _lastScrubCacheFrame = metadata.FixedFrame;

            _store.InsertOrReplaceAnchorAt(anchor);
            EnforceCapacityLimits();

            _log.Debug("Captured anchor at frame {0}", metadata.FixedFrame);
            return true;
        }

        /// <summary>
        /// Remove the bookmark at <paramref name="frame"/>, if any. Returns
        /// true iff a bookmark was found and removed.
        /// </summary>
        public bool RemoveBookmarkAtFrame(int frame) => _store.RemoveBookmarkAt(frame);

        /// <summary>
        /// Find the nearest anchor whose frame is strictly less than the
        /// world's current frame and JumpToFrame to it. No-op if the recorder
        /// is not running or there is no such anchor.
        /// </summary>
        public bool JumpToPreviousAnchor()
        {
            if (_state == BufferState.Idle || _core.World.IsDisposed)
            {
                return false;
            }
            var current = _core.Accessor.FixedFrame;
            int target = int.MinValue;
            var anchors = _store.Anchors;
            for (int i = anchors.Count - 1; i >= 0; i--)
            {
                if (anchors[i].FixedFrame < current)
                {
                    target = anchors[i].FixedFrame;
                    break;
                }
            }
            var bookmarks = _store.Bookmarks;
            for (int i = bookmarks.Count - 1; i >= 0; i--)
            {
                var f = bookmarks[i].FixedFrame;
                if (f < current && f > target)
                {
                    target = f;
                    break;
                }
            }
            if (target == int.MinValue)
            {
                _log.Debug("No previous anchor before frame {0}", current);
                return false;
            }
            return JumpToFrame(target);
        }

        /// <summary>
        /// Find the nearest anchor whose frame is strictly greater than the
        /// world's current frame and JumpToFrame to it. Useful while paused
        /// after a rewind, so the user can step through anchors.
        /// </summary>
        public bool JumpToNextAnchor()
        {
            if (_state == BufferState.Idle || _core.World.IsDisposed)
            {
                return false;
            }
            var current = _core.Accessor.FixedFrame;
            int target = int.MaxValue;
            var anchors = _store.Anchors;
            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i].FixedFrame > current)
                {
                    target = anchors[i].FixedFrame;
                    break;
                }
            }
            var bookmarks = _store.Bookmarks;
            for (int i = 0; i < bookmarks.Count; i++)
            {
                var f = bookmarks[i].FixedFrame;
                if (f > current && f < target)
                {
                    target = f;
                    break;
                }
            }
            if (target == int.MaxValue)
            {
                _log.Debug("No next anchor past frame {0}", current);
                return false;
            }
            return JumpToFrame(target);
        }

        // Toggles the input-history-locker registration in lockstep with
        // the recorder's active state. Centralizes the add/remove pair so
        // callers (Start, Stop, Reset, LoadRecordingFromFile, Dispose)
        // don't have to repeat the world-disposed guard or worry about
        // double-registration.
        void EnsureLockerRegistered(bool registered)
        {
            if (_lockerRegistered == registered)
            {
                return;
            }
            if (_core.World.IsDisposed)
            {
                _lockerRegistered = false;
                return;
            }
            if (registered)
            {
                _core.AddHistoryLocker(this);
            }
            else
            {
                _core.RemoveHistoryLocker(this);
            }
            _lockerRegistered = registered;
        }

        // Subscribed via Events.OnFixedUpdateCompleted so capture happens AFTER
        // `_elapsedFixedTime += step`, keeping (frame, elapsed) in lockstep.
        // FixedFrameChangeEvent (the obvious-looking alternative) fires between
        // counter++ and elapsed+=step, so a snapshot captured there would store
        // elapsed one tick behind the frame counter — and after LoadSnapshot the
        // resimulation could never reproduce that off-by-one state, leading to a
        // checksum mismatch on the very first verified snapshot.
        void OnFixedFrameChange()
        {
            using var _profile = TrecsProfiling.Start("AutoRecorder OnFixedFrameChange");
            var frame = _core.Accessor.FixedFrame;

            switch (_state)
            {
                case BufferState.Idle:
                    return;
                case BufferState.FastForwarding:
                    HandleFastForwardingTick(frame);
                    return;
                case BufferState.LoadedPlayback:
                    HandleLoadedPlaybackTick(frame);
                    return;
                case BufferState.Scrubbed:
                    HandleScrubbedTick(frame);
                    return;
                case BufferState.LiveCapture:
                    CaptureSnapshotIfDue();
                    return;
            }
        }

        // FF is the simulation re-running through a previously recorded range,
        // so it's just as valid a place to detect desyncs as the post-FF
        // Playback walk. Verifying here lets a single click-and-jump catch
        // divergence anywhere in the skipped range. We don't capture mid-FF
        // and don't let the truncation logic interpret FF advancement as
        // user-driven progression.
        void HandleFastForwardingTick(int frame)
        {
            VerifyChecksumAtFrame(frame);
            if (!_fastForwardTarget.HasValue || frame < _fastForwardTarget.Value)
            {
                return;
            }
            // Reached the FF target — settle into the destination state.
            _scrubbedFrame = frame;
            _fastForwardTarget = null;
            _state = _postFastForwardState;
        }

        // Loaded buffer: trailing snapshots are content, not stale state.
        // Walking through them just verifies; reaching the tail auto-pauses
        // (or loops, if LoopPlayback is on); stepping past the tail promotes
        // to LiveCapture so the user can continue recording from there.
        void HandleLoadedPlaybackTick(int frame)
        {
            // Clear the scrub marker on the first forward step past it so
            // the controller's mode reflects "playing through a loaded
            // recording" rather than "paused at a scrub".
            if (_scrubbedFrame.HasValue && frame > _scrubbedFrame.Value)
            {
                _scrubbedFrame = null;
            }
            if (frame < LatestCapturedFrame)
            {
                VerifyChecksumAtFrame(frame);
                return;
            }
            if (frame == LatestCapturedFrame)
            {
                // First arrival at the loaded buffer's tail. Verify the
                // tail snapshot first — if a desync occurred between the
                // previous snapshot and here, this is the user's last
                // chance to catch it within this buffer.
                VerifyChecksumAtFrame(frame);
                if (_core.World.IsDisposed)
                {
                    return;
                }
                if (_loopPlayback)
                {
                    BeginLoopRewind();
                    return;
                }
                // Default: stop so the user notices we've reached the end.
                // From here the user can scrub back, click Record (Fork) to
                // commit + go live, or press Play to promote past the tail.
                _core.Accessor.GetSystemRunner().FixedIsPaused = true;
                return;
            }
            // frame > tail: user explicitly resumed past the recording's
            // tail. Promote to regular auto-recording from here so capture
            // continues live without a pause-loop.
            _state = BufferState.LiveCapture;
            DropAbandonedTimelineInputs();
            CaptureSnapshotIfDue();
        }

        // Auto-recording buffer the user scrubbed back into. Pressing Play
        // walks forward through the existing snapshots rather than over-
        // writing them. At the tail two actions:
        //   * Loop on  → rewind to the start, keep playing.
        //   * Loop off → pause. The user has to press Record (which Forks
        //                during Playback) to commit and go live. Auto-
        //                promoting at the tail used to be the default but
        //                that's exactly what the explicit Record/Fork
        //                action does, so making it implicit just confused
        //                the interaction with the Loop toggle.
        void HandleScrubbedTick(int frame)
        {
            if (frame < LatestCapturedFrame)
            {
                // Still inside the buffer — let it play through. Don't
                // capture (we already have a snapshot for this region) and
                // don't trim.
                VerifyChecksumAtFrame(frame);
                return;
            }
            // frame >= LatestCapturedFrame: caught up to the live edge.
            // Verify the tail snapshot first (last chance to catch a desync
            // inside the existing buffer).
            VerifyChecksumAtFrame(frame);
            if (_core.World.IsDisposed)
            {
                return;
            }
            if (_loopPlayback)
            {
                BeginLoopRewind();
                return;
            }
            _core.Accessor.GetSystemRunner().FixedIsPaused = true;
        }

        // Pause the runner now, then schedule a JumpToFrame(<start>) on
        // the next editor tick. Two reasons to defer via
        // EditorApplication.delayCall: (1) JumpToFrame disposes and
        // re-creates _frameSubscription, which is the very subscription
        // dispatching the OnFixedFrameChange call that triggered this; (2)
        // the current tick needs to unwind cleanly. *Critical*: the pause
        // must happen *before* scheduling — otherwise the next fixed
        // update fires before delayCall lands, advancing frame past
        // LatestCapturedFrame and hitting the "promote past tail" branch,
        // which flips _state to LiveCapture. By the time delayCall runs
        // the recorder is in live capture and the loop silently fails
        // (the bug we shipped initially).
        void BeginLoopRewind()
        {
            // Loop target is the earliest scrubbable frame across anchors,
            // snapshots, and the scrub cache — not _startFrame, which can
            // predate everything when the anchor cap has rolled. Called
            // only from tail branches, so at least one of the three lists
            // is non-empty.
            _core.Accessor.GetSystemRunner().FixedIsPaused = true;
            var loopTarget = _store.FindEarliest().frame;
            EditorApplication.delayCall += () =>
            {
                if (_state == BufferState.Idle || _core.World.IsDisposed)
                {
                    return;
                }
                if (JumpToFrame(loopTarget))
                {
                    // JumpToFrame pauses on a backward seek; un-pause so
                    // the loop keeps playing.
                    _core.Accessor.GetSystemRunner().FixedIsPaused = false;
                }
            };
        }

        // Called whenever the recorder commits to a new live edge — fork,
        // reset, or auto-promotion (scrubbed-back auto-recording reaching
        // its tail, loaded recording resumed past its tail). Drops queued
        // inputs at the current frame and beyond so the resumed live
        // recording starts with a clean queue.
        //
        // Including the current frame matters: callers reach this point
        // *before* the current frame's input phase has run in the new
        // (post-scrub or post-load) timeline — JumpToFrame's FF break
        // happens just past the increment to target, and JumpToFrame's
        // no-FF branch pauses immediately after LoadSnapshot. Either way
        // the queued input at the current frame is leftover from the
        // abandoned timeline and would assert ("input already exists")
        // when the next live tick's input-phase system AddInputs.
        void DropAbandonedTimelineInputs()
        {
            if (_core.World.IsDisposed)
            {
                return;
            }
            _core
                .Accessor.GetEntityInputQueue()
                .ClearFutureInputsAfterOrAt(_core.Accessor.FixedFrame);
        }

        // Two independent cadences fire from this single tick handler:
        // anchors (sparse, persisted) and scrub-cache (dense, transient).
        // Checksums are derived from the snapshot bytes produced here —
        // no separate checksum cadence. When both anchor and scrub are
        // due the snapshot bytes go to the anchor list; the entry there
        // serves both navigation roles, so adding it to the scrub cache
        // too would just duplicate bytes for no win.
        void CaptureSnapshotIfDue()
        {
            if (_core.World.IsDisposed)
            {
                return;
            }

            var fixedDeltaTime = _core.Accessor.GetSystemRunner().FixedDeltaTime;
            var now = _core.Accessor.FixedFrame;
            var anchorDue =
                !_lastAnchorFrame.HasValue
                || (now - _lastAnchorFrame.Value) * fixedDeltaTime
                    >= _settings.AnchorIntervalSeconds;
            var scrubDue =
                !_lastScrubCacheFrame.HasValue
                || (now - _lastScrubCacheFrame.Value) * fixedDeltaTime
                    >= _settings.ScrubCacheIntervalSeconds;

            if (!anchorDue && !scrubDue)
            {
                return;
            }

            using var _profile = TrecsProfiling.Start("AutoRecorder CaptureSnapshot");

            var metadata = _core.SnapshotSerializer.SaveSnapshot(
                _settings.Version,
                out var payload,
                out var checksum,
                includeTypeChecks: true
            );
            _store.SetChecksum(metadata.FixedFrame, checksum);
            var anchor = new WorldSnapshot
            {
                FixedFrame = metadata.FixedFrame,
                Kind = SnapshotKind.Anchor,
                Label = "",
                Payload = payload,
            };

            if (anchorDue)
            {
                _store.AppendAnchor(anchor);
                _lastAnchorFrame = metadata.FixedFrame;
                _lastScrubCacheFrame = metadata.FixedFrame;
            }
            else
            {
                _store.AppendScrubCacheEntry(anchor);
                _lastScrubCacheFrame = metadata.FixedFrame;
            }

            EnforceCapacityLimits();
        }

        // Return the desync live snapshot's payload buffer to the pool and
        // clear the field. Called wherever _desyncedFrame is cleared so the
        // two stay in lockstep.
        void ClearDesyncLiveSnapshot()
        {
            if (_desyncLiveSnapshot != null)
            {
                _pool.Return(_desyncLiveSnapshot.Payload);
                _desyncLiveSnapshot = null;
            }
        }

        // Verify the simulation produced the same world state we captured
        // at this frame. Called from OnFixedFrameChange while the user is
        // walking forward inside the buffer (Playback mode). Sets the desync
        // marker on first mismatch and stops checking — once desynced the
        // same buffer can't trust further checksums anyway.
        void VerifyChecksumAtFrame(int frame)
        {
            if (_desyncedFrame.HasValue || _core.World.IsDisposed)
            {
                return;
            }
            // _store.Checksums holds entries for snapshot capture frames
            // (anchors + scrub-cache); absent key = "no checksum at this
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
                _core.SnapshotSerializer.SaveSnapshot(
                    _settings.Version,
                    out var livePayload,
                    out _,
                    includeTypeChecks: true
                );
                _desyncLiveSnapshot = new WorldSnapshot
                {
                    FixedFrame = frame,
                    Kind = SnapshotKind.Bookmark,
                    Label = "desync-live",
                    Payload = livePayload,
                };

                _desyncedFrame = frame;
                _log.Warning(
                    "Desync at frame {0}: expected checksum {1} but got {2} "
                        + "(simulation re-run from an earlier anchor produced "
                        + "different state — non-determinism in your code or data).",
                    frame,
                    expected,
                    actual
                );
                EditorApplication.isPaused = true;
            }
        }

#if DEBUG
        /// <summary>
        /// Dump both the recorded and live (diverged) world states at the
        /// desynced frame as flat-path text files and open them. Diff the two
        /// files to see exactly which fields diverged.
        /// </summary>
        public (string recordedPath, string livePath)? DumpDesyncDiff()
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

            DumpSnapshotToFlatPath(_desyncLiveSnapshot.Payload, livePath);
            DumpSnapshotToFlatPath(recordedSnapshot.Value.payload, recordedPath);

            _log.Info("Desync diff dumped:\n  recorded: {0}\n  live: {1}", recordedPath, livePath);
            return (recordedPath, livePath);
        }

        void DumpSnapshotToFlatPath(ReadOnlyMemory<byte> payload, string path)
        {
            _core.SnapshotSerializer.LoadSnapshot(payload);

            using var fileStream = File.Create(path);
            using var streamWriter = new StreamWriter(fileStream);

            var writer = new FlatPathSerializationWriter(streamWriter, _serializerRegistry);
            writer.Start(version: _settings.Version, flags: SerializationFlags.DesyncFriendlyHeaps);
            _stateSerializer.SerializeFullState(writer);
            writer.Complete();
        }
#endif

        // Anchors are sparse so they're capped by count; scrub cache is
        // dense so it's capped by bytes. Bookmarks are user-controlled and
        // never auto-evicted.
        void EnforceCapacityLimits()
        {
            var evicted = _store.EnforceAnchorCountCap(_settings.MaxAnchorCount);
            if (evicted > 0 && _store.Anchors.Count > 0)
            {
                // The recording's earliest scrubbable frame moved forward;
                // track that and prune queued inputs that predate it.
                // Bookmarks before the new earliest are intentionally kept —
                // the user placed them deliberately and they're still
                // scrubbable on their own.
                _startFrame = _store.Anchors[0].FixedFrame;
                if (!_core.World.IsDisposed)
                {
                    _core
                        .Accessor.GetEntityInputQueue()
                        .ClearInputsBeforeOrAt(MaxClearFrame ?? _startFrame - 1);
                }
            }

            _store.EnforceScrubCacheBytesCap(_settings.MaxScrubCacheBytes);
        }
    }

    /// <summary>
    /// Authoritative playhead + buffer-kind state of a
    /// <see cref="TrecsRewindBuffer"/>. Replaces what used to be an implicit
    /// machine across three nullable / boolean fields.
    ///
    /// Transitions (all driven by <see cref="TrecsRewindBuffer"/>'s public
    /// methods and the FixedUpdateCompleted event handler):
    /// <list type="bullet">
    /// <item><c>Idle → LiveCapture</c> on <c>Start</c>.</item>
    /// <item><c>LiveCapture → Scrubbed</c> on backward <c>JumpToFrame</c>
    /// (no FF needed).</item>
    /// <item><c>LiveCapture | Scrubbed | LoadedPlayback → FastForwarding</c>
    /// on <c>JumpToFrame</c> with target &gt; nearest snapshot frame.</item>
    /// <item><c>FastForwarding → Scrubbed | LoadedPlayback</c> when FF
    /// target is reached (determined by
    /// <c>_postFastForwardState</c>).</item>
    /// <item><c>Scrubbed → LiveCapture</c> on <c>ForkAtCurrentFrame</c>
    /// (commits scrub, truncates trailing buffer, resumes live capture).</item>
    /// <item><c>LoadedPlayback → LiveCapture</c> on the first fixed tick
    /// past the loaded buffer's tail (promote to auto-recording), or on
    /// <c>ForkAtCurrentFrame</c>.</item>
    /// <item><c>Idle ← any</c> on <c>Stop / Dispose</c>; <c>LiveCapture</c>
    /// re-entry on <c>Reset</c>.</item>
    /// <item><c>LoadedPlayback</c> entered from any state via
    /// <c>LoadRecordingFromFile</c>.</item>
    /// </list>
    /// </summary>
    public enum BufferState
    {
        /// <summary>Recorder is stopped; no capture, no playback.</summary>
        Idle,

        /// <summary>
        /// Recording at the live edge of an auto-recording buffer. Each
        /// fixed tick may capture (anchor / scrub-cache / checksum) per
        /// cadence rules.
        /// </summary>
        LiveCapture,

        /// <summary>
        /// Playhead landed inside an auto-recording buffer after a scrub
        /// back. Paused; trailing buffer is tentatively stale. Pressing
        /// Record (Fork) commits the scrub frame as the new live edge and
        /// drops the trailing buffer.
        /// </summary>
        Scrubbed,

        /// <summary>
        /// Fast-forward in progress towards
        /// <c>_fastForwardTarget</c>. On reaching the target the recorder
        /// transitions to <c>_postFastForwardState</c>.
        /// </summary>
        FastForwarding,

        /// <summary>
        /// Playing through a buffer loaded from a saved <c>.trec</c> file.
        /// Trailing buffer is never truncated; reaching the tail auto-pauses
        /// (or loops, if <see cref="TrecsRewindBuffer.LoopPlayback"/> is on).
        /// Stepping past the tail promotes to <c>LiveCapture</c>.
        /// </summary>
        LoadedPlayback,
    }

    /// <summary>
    /// Header summary parsed from a saved recording file. Exposed so editor
    /// tooling can inspect frame span / tick rate without loading the full
    /// snapshot list. See <see cref="TrecsRewindBuffer.TryReadRecordingHeader"/>.
    /// </summary>
    internal readonly struct RecordingHeader
    {
        public readonly int StartFrame;
        public readonly int EndFrame;
        public readonly float FixedDeltaTime;

        public RecordingHeader(int startFrame, int endFrame, float fixedDeltaTime)
        {
            StartFrame = startFrame;
            EndFrame = endFrame;
            FixedDeltaTime = fixedDeltaTime;
        }

        public int FrameCount => EndFrame - StartFrame + 1;
        public float DurationSeconds => FrameCount * FixedDeltaTime;
    }
}
