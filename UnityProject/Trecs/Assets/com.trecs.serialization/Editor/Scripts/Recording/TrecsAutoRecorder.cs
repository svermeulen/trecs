// #define ENABLE_DESYNC_DEBUGGING

using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;
using UnityEditor;
using UnityEngine;
#if ENABLE_DESYNC_DEBUGGING
using Newtonsoft.Json;
#endif

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Periodically captures full-state snapshots of a Trecs <see cref="World"/>
    /// while running. Snapshots are kept in memory only — no disk I/O. Designed
    /// to be enabled on demand by editor tooling (see <c>TrecsPlayerWindow</c>).
    /// </summary>
    public class TrecsAutoRecorder : IDisposable, IInputHistoryLocker
    {
#if ENABLE_DESYNC_DEBUGGING
        // JSON snapshots for diff-driven desync diagnosis. One file per
        // captured live snapshot and one per replay verification. Diff
        // workflow: `diff recording_snapshot<frame>.json
        // playback_snapshot<frame>.json` after the first reported desync.
        const string SnapshotsDirName = SvkjDebugConstants.TempDirName + "/snapshots";
#endif

        static readonly TrecsLog _log = TrecsLog.Default;

        readonly World _world;
        readonly IWorldStateSerializer _stateSerializer;
        readonly SerializerRegistry _serializerRegistry;
        readonly TrecsAutoRecorderSettings _settings;
        readonly SnapshotSerializer _snapshotSerializer;
        readonly RecordingBundleSerializer _bundleSerializer;
        readonly List<BundleAnchor> _anchors = new();
        readonly List<BundleSnapshot> _snapshots = new();

        // Transient: rolling cache of recent snapshots used to make scrub-back
        // instant. Captured at ScrubCacheIntervalSeconds cadence (much denser
        // than anchors), capped by MaxScrubCacheBytes (drop oldest), and never
        // saved to disk. Cleared on Start/Reset/Fork/Trim/Load — i.e. any
        // operation that mutates the timeline.
        readonly List<BundleAnchor> _scrubCache = new();
        long _scrubCacheBytes;
#if !TRECS_IS_PROFILING
        // Per-frame checksums captured at ChecksumFrameInterval cadence during
        // live recording. Saved into the bundle's Checksums dict so
        // BundlePlayer can detect desyncs close to where they happen rather
        // than only at sparse anchor frames. Mirrors the dict BundleRecorder
        // (the runtime recorder) maintains. Skipped under TRECS_IS_PROFILING
        // so editor profiling builds don't pay the per-frame hash cost —
        // anchor checksums still survive in BundleAnchor.Checksum.
        readonly DenseDictionary<int, uint> _checksums = new();
#endif

        WorldAccessor _accessor;
        RecordingChecksumCalculator _checksumCalculator;
        SerializationBuffer _checksumBuffer;
        IDisposable _frameSubscription;

        bool _isRecording;
        int _startFrame;

        // Frame of the most recent persisted-anchor capture, used by the
        // anchor-cadence timer in CaptureSnapshotIfDue. Set by Start/Reset
        // to a sentinel that forces immediate capture on the next frame.
        int _lastAnchorFrame;

        // Same idea for the transient scrub cache, on a separate (denser)
        // cadence.
        int _lastScrubCacheFrame;
        long _totalBytes;

        // The frame the user has most-recently scrubbed to (post-fast-forward
        // if applicable). While set, any snapshots past this frame are
        // *tentatively* stale — the user can keep scrubbing freely without
        // losing them. They get truncated only on the first user-driven
        // (non-fast-forward) fixed-update tick that advances the simulation
        // past this point. Null while a fast-forward is in progress (no stable
        // position yet) and after a truncation has been committed.
        int? _pendingDivergenceFrame;

        // Target frame of an active fast-forward triggered by JumpToFrame.
        // Set when JumpToFrame asks SystemRunner to FF; cleared when the FF
        // reaches that frame in OnFixedFrameChange. While set, FF inner-loop
        // ticks are skipped for capture/truncation purposes (we don't want to
        // capture mid-FF snapshots, and we don't want the truncation logic to
        // mistake FF progression for user-driven progression).
        int? _fastForwardTargetFrame;

        // True iff the current in-memory buffer was loaded from a saved
        // recording file (LoadRecordingFromFile). While set:
        //   * Trailing snapshots past the divergence point are NOT trimmed
        //     when simulation advances — the loaded recording's "future" is
        //     preserved so the user can scrub it again.
        //   * When the simulation reaches the last loaded snapshot, fixed
        //     phase auto-pauses (the recording's content is exhausted).
        // Cleared on Start, Reset, and ForkAtCurrentFrame so the buffer
        // becomes a regular auto-recording.
        bool _isLoadedRecording;

        // Tracks whether we are currently registered with the EntityInputQueue
        // as an IInputHistoryLocker. Add/remove operations are gated through
        // EnsureLockerRegistered so we never double-register or double-remove.
        bool _lockerRegistered;

        // First frame at which we observed a stored-vs-recomputed checksum
        // mismatch during the current Playback walk. Null while the buffer
        // looks consistent. Cleared on Start, Reset, Fork, JumpToFrame
        // (every scrub gets a fresh chance to verify), and load-from-file.
        int? _desyncedFrame;

        public TrecsAutoRecorder(
            World world,
            IWorldStateSerializer stateSerializer,
            SerializerRegistry serializerRegistry,
            TrecsAutoRecorderSettings settings,
            SnapshotSerializer snapshotSerializer
        )
        {
            if (settings.ChecksumFrameInterval < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(settings)
                        + "."
                        + nameof(TrecsAutoRecorderSettings.ChecksumFrameInterval),
                    settings.ChecksumFrameInterval,
                    "ChecksumFrameInterval must be >= 1"
                );

            _world = world;
            _stateSerializer = stateSerializer;
            _serializerRegistry = serializerRegistry;
            _settings = settings;
            _snapshotSerializer = snapshotSerializer;
            _bundleSerializer = new RecordingBundleSerializer(serializerRegistry);
        }

        public World World => _world;
        public bool IsRecording => _isRecording;
        public int StartFrame => _startFrame;

        /// <summary>
        /// Rightmost captured frame across persisted anchors, user snapshots,
        /// and the transient scrub cache. Drives the player UI's slider end
        /// position and the "at live edge" check.
        /// </summary>
        public int LastAnchorFrame
        {
            get
            {
                int max = int.MinValue;
                if (_anchors.Count > 0)
                    max = _anchors[_anchors.Count - 1].FixedFrame;
                if (_snapshots.Count > 0 && _snapshots[_snapshots.Count - 1].FixedFrame > max)
                    max = _snapshots[_snapshots.Count - 1].FixedFrame;
                if (_scrubCache.Count > 0 && _scrubCache[_scrubCache.Count - 1].FixedFrame > max)
                    max = _scrubCache[_scrubCache.Count - 1].FixedFrame;
                return max == int.MinValue ? _startFrame : max;
            }
        }

        public IReadOnlyList<BundleAnchor> Anchors => _anchors;

        /// <summary>
        /// User-placed full-state snapshots with labels. Independent of
        /// auto-captured <see cref="Anchors"/>: deleting a snapshot never
        /// affects an anchor at the same frame, and snapshots are not subject
        /// to the auto-recorder's capacity caps.
        /// </summary>
        public IReadOnlyList<BundleSnapshot> Snapshots => _snapshots;

        public long TotalBytes => _totalBytes;

        /// <summary>
        /// True when the world is at the recorder's live edge — i.e. there is
        /// no pending divergence from a prior scrub-back. Transitions to false
        /// when <see cref="JumpToFrame"/> rewinds the world; transitions back
        /// to true when the simulation advances past the divergence point with
        /// live input (truncating the trailing snapshots). Drives the
        /// Recording vs Playback distinction in the controller.
        /// </summary>
        public bool IsAtLiveEdge =>
            !_pendingDivergenceFrame.HasValue && !_fastForwardTargetFrame.HasValue;

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
        /// True iff the in-memory buffer came from a loaded recording file.
        /// Loaded buffers preserve their trailing snapshots (the recording's
        /// future) when simulation advances, and auto-pause when the loaded
        /// buffer is exhausted. Cleared by Start/Reset/Fork.
        /// </summary>
        public bool IsLoadedRecording => _isLoadedRecording;

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
        bool _isLoopingPlayback;

        /// <summary>
        /// Session-local toggle. When true, reaching the last snapshot of a
        /// loaded recording rewinds to the start frame and continues playing
        /// instead of pausing.
        /// </summary>
        public bool LoopPlayback
        {
            get => _isLoopingPlayback;
            set => _isLoopingPlayback = value;
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
                        ? _anchors.Count / (float)_settings.MaxAnchorCount
                        : 0f;
                var byScrubBytes =
                    _settings.MaxScrubCacheBytes > 0
                        ? _scrubCacheBytes / (float)_settings.MaxScrubCacheBytes
                        : 0f;
                return Mathf.Clamp01(Mathf.Max(byAnchorCount, byScrubBytes));
            }
        }

        /// <summary>
        /// Frame at which the user last scrubbed back, or null if at the live
        /// edge. Exposed for UI purposes (e.g. showing where the divergence
        /// will commit if simulation advances past it).
        /// </summary>
        public int? PendingDivergenceFrame => _pendingDivergenceFrame;

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
                if (!_isRecording)
                {
                    return null;
                }
                // Use the earliest scrubbable frame across anchors, snapshots,
                // and the scrub cache — replaying from any of those needs
                // inputs from that frame onward. Falls back to _startFrame
                // pre-first-capture.
                int earliest = int.MaxValue;
                if (_anchors.Count > 0)
                    earliest = _anchors[0].FixedFrame;
                if (_snapshots.Count > 0 && _snapshots[0].FixedFrame < earliest)
                    earliest = _snapshots[0].FixedFrame;
                if (_scrubCache.Count > 0 && _scrubCache[0].FixedFrame < earliest)
                    earliest = _scrubCache[0].FixedFrame;
                if (earliest == int.MaxValue)
                    earliest = _startFrame;
                return earliest - 1;
            }
        }

        public void Initialize()
        {
            _accessor = _world.CreateAccessor(AccessorRole.Unrestricted, "TrecsAutoRecorder");
            _checksumCalculator = new RecordingChecksumCalculator(_stateSerializer);
            _checksumBuffer = new SerializationBuffer(_serializerRegistry);
        }

        public void Dispose()
        {
            Stop();
            _checksumBuffer?.Dispose();
            _checksumBuffer = null;
            _bundleSerializer.Dispose();
        }

        public void Start()
        {
            if (_isRecording)
            {
                return;
            }

            _anchors.Clear();
            _snapshots.Clear();
            _scrubCache.Clear();
#if !TRECS_IS_PROFILING
            _checksums.Clear();
#endif
            _totalBytes = 0;
            _scrubCacheBytes = 0;
            _startFrame = _accessor.FixedFrame;
            // Force the first FixedUpdateCompleted tick to capture an anchor
            // immediately. We don't capture here in Start() because the
            // activator may invoke us during Layer.Initialize, before
            // downstream serializers (e.g. Orca's LuaStateSerializer) have
            // finished their own init.
            _lastAnchorFrame = _startFrame - int.MaxValue / 2;
            _lastScrubCacheFrame = _startFrame - int.MaxValue / 2;
            _pendingDivergenceFrame = null;
            _fastForwardTargetFrame = null;
            _isLoadedRecording = false;
            _isLoopingPlayback = false;
            _desyncedFrame = null;
            _isRecording = true;
            // Fresh recording — discard any prior backing-file name so a
            // subsequent "Save" prompts for a new name rather than
            // overwriting the previously-loaded slot.
            LoadedRecordingPath = null;

#if ENABLE_DESYNC_DEBUGGING
            // Wipe stale snapshots from a previous run so the diff workflow
            // doesn't accidentally compare frames from different sessions.
            if (Directory.Exists(SnapshotsDirName))
            {
                Directory.Delete(SnapshotsDirName, true);
            }
            Directory.CreateDirectory(SnapshotsDirName);
#endif

            // Lock input history at-or-after _startFrame so the queue's
            // periodic cleanup doesn't prune frames the user might want to
            // scrub back to. Without this, scrub-back + Play replays no
            // inputs because they were already discarded as "old".
            EnsureLockerRegistered(true);

            _frameSubscription = _accessor.Events.OnFixedUpdateCompleted(OnFixedFrameChange);

            _log.Debug("Auto recording started at fixed frame {}", _startFrame);
        }

        public void Stop()
        {
            if (!_isRecording)
            {
                return;
            }

            _frameSubscription?.Dispose();
            _frameSubscription = null;
            _isRecording = false;
            EnsureLockerRegistered(false);

            _log.Debug(
                "Auto recording stopped — captured {} anchors ({} bytes total)",
                _anchors.Count,
                _totalBytes
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
            if (!_isRecording)
            {
                _log.Warning("JumpToFrame called while not recording");
                return false;
            }
            if (_world.IsDisposed)
            {
                return false;
            }

            var nearest = FindNearestPersistedAtOrBefore(targetFrame);
            if (nearest == null)
            {
                if (_anchors.Count == 0 && _snapshots.Count == 0 && _scrubCache.Count == 0)
                {
                    _log.Warning("No anchors recorded yet — cannot jump");
                    return false;
                }
                // Target precedes everything in the buffer. Snap up to the
                // earliest scrubbable frame across anchors / scrub cache /
                // snapshots.
                nearest = FindEarliestPersisted();
                targetFrame = nearest.Value.frame;
            }

            var (anchorFrame, anchorData) = nearest.Value;
            var runner = _accessor.GetSystemRunner();

            // Detach from frame events so we don't capture a "snapshot" of the
            // half-restored state during the load itself.
            _frameSubscription?.Dispose();
            _frameSubscription = null;

            try
            {
                using (var stream = new MemoryStream(anchorData, writable: false))
                {
                    _snapshotSerializer.LoadSnapshot(stream);
                }

                // NOTE: we deliberately do NOT clear future inputs here.
                // Those inputs ARE the recording's content — clearing them
                // would mean Play (or fast-forward through the buffer)
                // re-walks the timeline with no input data, so the player
                // wouldn't move. The locker prevents the queue's normal
                // cleanup from pruning them. They're discarded only on an
                // explicit Fork or Reset, which truly abandon the timeline.

                if (targetFrame > _accessor.FixedFrame)
                {
                    // FastForward requires !FixedIsPaused; SystemRunner
                    // re-pauses automatically once the target frame is reached.
                    // The pending divergence frame is set to the FF target when
                    // the FF completes (in OnFixedFrameChange).
                    _fastForwardTargetFrame = targetFrame;
                    _pendingDivergenceFrame = null;
                    runner.FixedIsPaused = false;
                    runner.FastForwardTargetFrame = targetFrame;
                }
                else
                {
                    // Already at the loaded frame; that frame is now the user's
                    // scrub position. Snapshots past it are tentatively stale.
                    _fastForwardTargetFrame = null;
                    _pendingDivergenceFrame = _accessor.FixedFrame;
                    runner.FixedIsPaused = true;
                }

                // Each new scrub gets a fresh chance to verify checksums.
                // The previous desync (if any) was tied to the prior walk;
                // clear it so the user can scrub past the suspected frame
                // and watch for it again, or jump elsewhere and observe.
                _desyncedFrame = null;

                // Re-subscribe so further frames continue to snapshot normally.
                _frameSubscription = _accessor.Events.OnFixedUpdateCompleted(OnFixedFrameChange);
            }
            catch (Exception e)
            {
                // World state may be partially loaded — keep recording running
                // would silently capture corrupt snapshots. Stop cleanly so the
                // user sees auto-recording as inactive and can decide to
                // restart.
                _log.Error("JumpToFrame to snapshot @ frame {} failed: {}", anchorFrame, e);
                _isRecording = false;
                _fastForwardTargetFrame = null;
                _pendingDivergenceFrame = null;
                return false;
            }

            _log.Debug("Jumped to frame {} via snapshot at frame {}", targetFrame, anchorFrame);
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
                var registry = SerializationFactory.CreateRegistry();
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
            if (!_isRecording)
            {
                _log.Warning("SaveRecordingToFile called while not recording");
                return false;
            }
            if (_anchors.Count == 0)
            {
                _log.Warning("SaveRecordingToFile: no anchors to save");
                return false;
            }

            var fixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
            var blobs = new DenseHashSet<BlobId>();
            _world.GetBlobCache().GetAllActiveBlobIds(blobs);

            // EntityInputQueue is serialized via its own SerializationBuffer
            // envelope; the resulting bytes are an opaque payload inside the
            // outer bundle. Same shape BundlePlayer expects when reading
            // the queue out on load.
            byte[] queueBytes;
            using (var queueBuffer = new SerializationBuffer(_serializerRegistry))
            {
                queueBuffer.StartWrite(version: _settings.Version, includeTypeChecks: true);
                _accessor
                    .GetEntityInputQueue()
                    .Serialize(new TrecsSerializationWriterAdapter(queueBuffer));
                queueBuffer.EndWrite();
                queueBytes = queueBuffer.MemoryStream.ToArray();
            }

            // The in-memory anchor list is treated as the initial snapshot
            // (first entry) plus a sequence of trailing anchors (the rest).
            // User-placed snapshots live in their own list and round-trip
            // independently. The per-frame checksums dict is populated under
            // its own cadence in CaptureSnapshotIfDue and persisted into the
            // bundle so BundlePlayer can detect desyncs close to where they
            // happen rather than only at sparse anchor frames.
            var initial = _anchors[0];
            var anchors = new List<BundleAnchor>(_anchors.Count - 1);
            for (int i = 1; i < _anchors.Count; i++)
            {
                anchors.Add(_anchors[i]);
            }

            var bundle = new RecordingBundle
            {
                Header = new BundleHeader
                {
                    Version = _settings.Version,
                    StartFixedFrame = initial.FixedFrame,
                    // Current frame, not the last anchor's frame: per-frame
                    // checksums, scrub-cache entries, and snapshots can extend
                    // past the last anchor when their cadences differ. Matches
                    // BundleRecorder.Stop, which uses _accessor.FixedFrame.
                    EndFixedFrame = _accessor.FixedFrame,
                    FixedDeltaTime = fixedDeltaTime,
                    ChecksumFlags = SerializationFlags.IsForChecksum,
                    BlobIds = blobs,
                },
                InitialSnapshot = initial.Payload,
                InitialSnapshotChecksum = initial.Checksum,
                InputQueue = queueBytes,
                Checksums = CopyChecksums(),
                Anchors = anchors,
                Snapshots = _snapshots.ToArray(),
            };

            _bundleSerializer.Save(bundle, filePath);

            // Mark this file as the buffer's backing slot so a follow-up
            // "Save" overwrites it without reprompting (and so the Player
            // header shows the name).
            LoadedRecordingPath = filePath;

            _log.Debug(
                "Saved recording: {} anchors, {} snapshots, {} blob refs, {} bytes input queue",
                _anchors.Count,
                _snapshots.Count,
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
                _log.Warning("Recording file does not exist: {}", filePath);
                return false;
            }

            RecordingBundle bundle;
            try
            {
                bundle = _bundleSerializer.Load(filePath);
            }
            catch (Exception e)
            {
                _log.Error("Failed to read recording from {}: {}", filePath, e);
                return false;
            }

            if (bundle.InitialSnapshot == null || bundle.InitialSnapshot.Length == 0)
            {
                _log.Warning("Loaded recording has no initial snapshot");
                return false;
            }

            if (bundle.Header.Version != _settings.Version)
            {
                _log.Warning(
                    "Recording schema version {} does not match current {} — "
                        + "load may fail or the simulation may desync",
                    bundle.Header.Version,
                    _settings.Version
                );
            }

            var currentFixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
            if (!Mathf.Approximately(bundle.Header.FixedDeltaTime, currentFixedDeltaTime))
            {
                _log.Warning(
                    "Recording fixed delta time {} differs from current {} — input replay may desync",
                    bundle.Header.FixedDeltaTime,
                    currentFixedDeltaTime
                );
            }

            // Reconstruct the in-memory anchor list: initial snapshot at the
            // head, then trailing anchors. Persisted anchors and the transient
            // scrub cache are kept separate (anchors survive Save/Load; the
            // scrub cache is rebuilt per session).
            var loadedAnchors = new List<BundleAnchor>(bundle.Anchors.Count + 1)
            {
                new BundleAnchor
                {
                    FixedFrame = bundle.Header.StartFixedFrame,
                    Payload = bundle.InitialSnapshot,
                    Checksum = bundle.InitialSnapshotChecksum,
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
                using (var stream = new MemoryStream(earliest.Payload, writable: false))
                {
                    _snapshotSerializer.LoadSnapshot(stream);
                }

                // Wipe the live queue and replace it with the recording's
                // serialized inputs. ClearAllInputs (vs. ClearFutureInputsAfterOrAt)
                // is correct here: we're switching timelines wholesale, and any
                // pre-loaded-frame inputs on the live timeline don't apply.
                var inputQueue = _accessor.GetEntityInputQueue();
                inputQueue.ClearAllInputs();
                if (bundle.InputQueue.Length > 0)
                {
                    using var queueBuffer = new SerializationBuffer(_serializerRegistry);
                    queueBuffer.MemoryStream.Write(bundle.InputQueue, 0, bundle.InputQueue.Length);
                    queueBuffer.MemoryStream.Position = 0;
                    queueBuffer.StartRead();
                    inputQueue.Deserialize(new TrecsSerializationReaderAdapter(queueBuffer));
                    queueBuffer.StopRead(verifySentinel: false);
                }

                // Replace in-memory state with the loaded recording.
                _anchors.Clear();
                _anchors.AddRange(loadedAnchors);
                _snapshots.Clear();
                _snapshots.AddRange(bundle.Snapshots);
                _scrubCache.Clear();
                _scrubCacheBytes = 0;
#if !TRECS_IS_PROFILING
                // Preserve loaded per-frame checksums so a subsequent re-save
                // (post-fork or post-trim) doesn't lose desync-detection
                // coverage. The bundle's Checksums dict can be empty (older
                // editor-saved bundles) or null on older formats — fall back
                // to clearing.
                _checksums.Clear();
                if (bundle.Checksums != null)
                {
                    foreach (var (frame, checksum) in bundle.Checksums)
                    {
                        _checksums[frame] = checksum;
                    }
                }
#endif
                _totalBytes = 0;
                foreach (var b in _anchors)
                {
                    _totalBytes += b.Payload.LongLength;
                }
                _startFrame = bundle.Header.StartFixedFrame;
                _lastAnchorFrame = _anchors[_anchors.Count - 1].FixedFrame;
                _lastScrubCacheFrame = _startFrame - int.MaxValue / 2;
                _pendingDivergenceFrame = earliest.FixedFrame;
                _fastForwardTargetFrame = null;
                _isLoadedRecording = true;
                _isLoopingPlayback = false;
                _desyncedFrame = null;
                _isRecording = true;
                LoadedRecordingPath = filePath;

                // Hold input history for the loaded buffer's frame range so
                // the deserialized inputs aren't pruned by the queue's
                // normal cleanup before the user gets a chance to scrub.
                EnsureLockerRegistered(true);

                // Pause at the loaded frame so the user can scrub through.
                _accessor.GetSystemRunner().FixedIsPaused = true;

                _frameSubscription = _accessor.Events.OnFixedUpdateCompleted(OnFixedFrameChange);
            }
            catch (Exception e)
            {
                // Same recovery as JumpToFrame: world state may be partially
                // loaded; stop recording cleanly so the user can decide.
                _log.Error("LoadRecordingFromFile from {} failed: {}", filePath, e);
                _anchors.Clear();
                _snapshots.Clear();
                _scrubCache.Clear();
#if !TRECS_IS_PROFILING
                _checksums.Clear();
#endif
                _scrubCacheBytes = 0;
                _totalBytes = 0;
                _isRecording = false;
                _fastForwardTargetFrame = null;
                _pendingDivergenceFrame = null;
                _isLoadedRecording = false;
                _desyncedFrame = null;
                LoadedRecordingPath = null;
                EnsureLockerRegistered(false);
                return false;
            }

            _log.Debug(
                "Loaded recording with {} anchors (frames {} .. {})",
                _anchors.Count,
                _anchors[0].FixedFrame,
                _anchors[_anchors.Count - 1].FixedFrame
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
            if (!_isRecording || _world.IsDisposed)
            {
                return false;
            }
            var current = _accessor.FixedFrame;
            if (FindAnchorIndexAtOrBefore(current) < 0)
            {
                _log.Warning(
                    "ForkAtCurrentFrame: no anchor at or before frame {} — cannot fork",
                    current
                );
                return false;
            }
            TrimAnchorsAfter(current);
            TrimScrubCacheAfter(current);
            TrimSnapshotsAfter(current);
            TrimChecksumsAfter(current);
            DropAbandonedTimelineInputs();
            _pendingDivergenceFrame = null;
            _isLoadedRecording = false;
            _isLoopingPlayback = false;
            _desyncedFrame = null;
            // Keep LoadedRecordingPath attached so a subsequent plain Save
            // overwrites the original file — fork is the "edit this recording
            // from frame N onward" gesture. Save As remains the path for
            // saving under a different name.
            _log.Debug("Forked recording at frame {}; trailing anchors dropped", current);
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
            if (!_isRecording)
            {
                return 0;
            }
            var keepFrom = FindAnchorIndexAtOrBefore(frame);
            if (keepFrom <= 0)
            {
                return 0;
            }
            long droppedBytes = 0;
            for (var i = 0; i < keepFrom; i++)
            {
                droppedBytes += _anchors[i].Payload.Length;
            }
            _anchors.RemoveRange(0, keepFrom);
            _totalBytes -= droppedBytes;
            _startFrame = _anchors[0].FixedFrame;
            TrimSnapshotsBefore(_startFrame);
            TrimScrubCacheBefore(_startFrame);
            TrimChecksumsBefore(_startFrame);
            if (!_world.IsDisposed)
            {
                _accessor.GetEntityInputQueue().ClearInputsBeforeOrAt(_startFrame - 1);
            }
            _log.Debug(
                "Trimmed {} anchors before frame {} ({} bytes dropped)",
                keepFrom,
                frame,
                droppedBytes
            );
            return keepFrom;
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
            if (!_isRecording)
            {
                return 0;
            }
            var countBefore = _anchors.Count;
            var bytesBefore = _totalBytes;
            TrimAnchorsAfter(frame);
            TrimScrubCacheAfter(frame);
            TrimSnapshotsAfter(frame);
            TrimChecksumsAfter(frame);
            var dropped = countBefore - _anchors.Count;
            if (dropped == 0)
            {
                return 0;
            }
            if (!_world.IsDisposed)
            {
                // JumpToFrame deliberately preserves future inputs because they
                // ARE the recording's content — but here we're explicitly
                // discarding the post-frame timeline, so drop those inputs in
                // lockstep. Otherwise playing forward past `frame` would
                // re-consume the orphaned inputs and reproduce the same
                // gameplay the user just trimmed away.
                _accessor.GetEntityInputQueue().ClearFutureInputsAfterOrAt(frame + 1);
            }
            _log.Debug(
                "Trimmed {} anchors after frame {} ({} bytes dropped)",
                dropped,
                frame,
                bytesBefore - _totalBytes
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
            if (!_isRecording)
            {
                _log.Warning("Reset called while not recording");
                return;
            }
            _anchors.Clear();
            _snapshots.Clear();
            _scrubCache.Clear();
#if !TRECS_IS_PROFILING
            _checksums.Clear();
#endif
            _totalBytes = 0;
            _scrubCacheBytes = 0;
            _startFrame = _accessor.FixedFrame;
            _lastAnchorFrame = _startFrame - int.MaxValue / 2;
            _lastScrubCacheFrame = _startFrame - int.MaxValue / 2;
            _pendingDivergenceFrame = null;
            _fastForwardTargetFrame = null;
            _isLoadedRecording = false;
            _isLoopingPlayback = false;
            _desyncedFrame = null;
            LoadedRecordingPath = null;
            DropAbandonedTimelineInputs();
            _log.Debug("Auto recording reset at frame {}", _startFrame);
        }

        /// <summary>
        /// Capture a labeled full-state snapshot at the world's current
        /// fixed frame and add it to <see cref="Snapshots"/>. Replaces any
        /// existing snapshot at the same frame. Snapshots survive Save/Load
        /// and are independent of auto-captured anchors.
        /// </summary>
        /// <param name="label">Display label for the recorder UI. Empty
        /// string is allowed; null is rejected.</param>
        /// <returns>True if the snapshot was captured.</returns>
        public bool CaptureSnapshotAtCurrentFrame(string label)
        {
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }
            if (!_isRecording)
            {
                _log.Warning("CaptureSnapshotAtCurrentFrame called while not recording");
                return false;
            }
            if (_world.IsDisposed)
            {
                return false;
            }

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                _snapshotSerializer.SaveSnapshot(_settings.Version, ms, includeTypeChecks: true);
                bytes = ms.ToArray();
            }
            var checksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _settings.Version,
                _checksumBuffer,
                SerializationFlags.IsForChecksum
            );
            var snapshot = new BundleSnapshot
            {
                FixedFrame = _accessor.FixedFrame,
                Checksum = checksum,
                Label = label,
                Payload = bytes,
            };

            // Replace any existing snapshot at the same frame; otherwise
            // insert sorted by frame so navigation can scan in order.
            for (int i = 0; i < _snapshots.Count; i++)
            {
                if (_snapshots[i].FixedFrame == snapshot.FixedFrame)
                {
                    _snapshots[i] = snapshot;
                    _log.Debug(
                        "Replaced snapshot at frame {} (label='{}')",
                        snapshot.FixedFrame,
                        label
                    );
                    return true;
                }
            }
            int insertAt = 0;
            while (
                insertAt < _snapshots.Count && _snapshots[insertAt].FixedFrame < snapshot.FixedFrame
            )
            {
                insertAt++;
            }
            _snapshots.Insert(insertAt, snapshot);
            _log.Debug("Captured snapshot at frame {} (label='{}')", snapshot.FixedFrame, label);
            return true;
        }

        /// <summary>
        /// Capture an unlabeled full-state anchor at the world's current
        /// fixed frame, outside the normal
        /// <see cref="AnchorIntervalSeconds"/> cadence. Symmetric with
        /// <see cref="CaptureSnapshotAtCurrentFrame"/> but without a label —
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
            if (!_isRecording)
            {
                _log.Warning("CaptureAnchorAtCurrentFrame called while not recording");
                return false;
            }
            if (_world.IsDisposed)
            {
                return false;
            }

            using var stream = new MemoryStream();
            var metadata = _snapshotSerializer.SaveSnapshot(
                _settings.Version,
                stream,
                includeTypeChecks: true
            );
            var data = stream.ToArray();
            var checksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _settings.Version,
                _checksumBuffer,
                SerializationFlags.IsForChecksum
            );
            var anchor = new BundleAnchor
            {
                FixedFrame = metadata.FixedFrame,
                Payload = data,
                Checksum = checksum,
            };

            // Anchor cadence: a manual capture satisfies the "we have a
            // recovery point near here" need just as well as an auto one, so
            // pretend we just made an auto-capture. Same treatment for the
            // scrub-cache cadence — anchors double as scrub-cache entries
            // during navigation, so we reset that tracker too to avoid an
            // immediate redundant scrub-cache capture on the next tick.
            _lastAnchorFrame = metadata.FixedFrame;
            _lastScrubCacheFrame = metadata.FixedFrame;

            // Replace if an anchor already exists at this frame; otherwise
            // insert sorted so navigation can scan in order. Manual capture
            // can land at any frame (paused after a scrub-back, etc.), so
            // we can't just append.
            for (int i = 0; i < _anchors.Count; i++)
            {
                if (_anchors[i].FixedFrame == metadata.FixedFrame)
                {
                    _totalBytes -= _anchors[i].Payload.Length;
                    _anchors[i] = anchor;
                    _totalBytes += data.Length;
                    _log.Debug("Replaced anchor at frame {}", metadata.FixedFrame);
                    return true;
                }
            }
            int insertAt = 0;
            while (insertAt < _anchors.Count && _anchors[insertAt].FixedFrame < metadata.FixedFrame)
            {
                insertAt++;
            }
            _anchors.Insert(insertAt, anchor);
            _totalBytes += data.Length;

            EnforceCapacityLimits();

            _log.Debug("Captured anchor at frame {}", metadata.FixedFrame);
            return true;
        }

        /// <summary>
        /// Remove the snapshot at <paramref name="frame"/>, if any. Returns
        /// true iff a snapshot was found and removed.
        /// </summary>
        public bool RemoveSnapshotAtFrame(int frame)
        {
            for (int i = 0; i < _snapshots.Count; i++)
            {
                if (_snapshots[i].FixedFrame == frame)
                {
                    _snapshots.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Find the nearest anchor whose frame is strictly less than the
        /// world's current frame and JumpToFrame to it. No-op if the recorder
        /// is not running or there is no such anchor.
        /// </summary>
        public bool JumpToPreviousAnchor()
        {
            if (!_isRecording || _world.IsDisposed)
            {
                return false;
            }
            var current = _accessor.FixedFrame;
            int target = int.MinValue;
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors[i].FixedFrame < current)
                {
                    target = _anchors[i].FixedFrame;
                    break;
                }
            }
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                var f = _snapshots[i].FixedFrame;
                if (f < current && f > target)
                {
                    target = f;
                    break;
                }
            }
            if (target == int.MinValue)
            {
                _log.Debug("No previous anchor before frame {}", current);
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
            if (!_isRecording || _world.IsDisposed)
            {
                return false;
            }
            var current = _accessor.FixedFrame;
            int target = int.MaxValue;
            for (int i = 0; i < _anchors.Count; i++)
            {
                if (_anchors[i].FixedFrame > current)
                {
                    target = _anchors[i].FixedFrame;
                    break;
                }
            }
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var f = _snapshots[i].FixedFrame;
                if (f > current && f < target)
                {
                    target = f;
                    break;
                }
            }
            if (target == int.MaxValue)
            {
                _log.Debug("No next anchor past frame {}", current);
                return false;
            }
            return JumpToFrame(target);
        }

        int FindAnchorIndexAtOrBefore(int frame)
        {
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors[i].FixedFrame <= frame)
                {
                    return i;
                }
            }
            return -1;
        }

        // Find the nearest scrubbable anchor whose frame is <= target,
        // searching anchors, snapshots, and the transient scrub cache. All
        // three lists are kept sorted by frame ascending. On a tie at the
        // same frame, prefers anchors > scrub cache > snapshots (anchors
        // are guaranteed-live-timeline bytes; scrub-cache entries reflect
        // the same timeline up to drop-oldest eviction; snapshots may
        // capture an earlier divergent moment).
        (int frame, byte[] data)? FindNearestPersistedAtOrBefore(int target)
        {
            int bestFrame = int.MinValue;
            byte[] bestData = null;
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors[i].FixedFrame <= target)
                {
                    bestFrame = _anchors[i].FixedFrame;
                    bestData = _anchors[i].Payload;
                    break;
                }
            }
            for (int i = _scrubCache.Count - 1; i >= 0; i--)
            {
                if (_scrubCache[i].FixedFrame <= target)
                {
                    if (_scrubCache[i].FixedFrame > bestFrame)
                    {
                        bestFrame = _scrubCache[i].FixedFrame;
                        bestData = _scrubCache[i].Payload;
                    }
                    break;
                }
            }
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                var b = _snapshots[i];
                if (b.FixedFrame <= target)
                {
                    if (b.FixedFrame > bestFrame)
                    {
                        bestFrame = b.FixedFrame;
                        bestData = b.Payload;
                    }
                    break;
                }
            }
            return bestData == null ? null : (bestFrame, bestData);
        }

        // Earliest scrubbable anchor across anchors, snapshots, and the
        // scrub cache, used when JumpToFrame's target precedes everything
        // in the buffer (e.g. the slider's leftmost position).
        (int frame, byte[] data) FindEarliestPersisted()
        {
            int frame = int.MaxValue;
            byte[] data = null;
            if (_anchors.Count > 0)
            {
                frame = _anchors[0].FixedFrame;
                data = _anchors[0].Payload;
            }
            if (_scrubCache.Count > 0 && _scrubCache[0].FixedFrame < frame)
            {
                frame = _scrubCache[0].FixedFrame;
                data = _scrubCache[0].Payload;
            }
            if (_snapshots.Count > 0 && _snapshots[0].FixedFrame < frame)
            {
                frame = _snapshots[0].FixedFrame;
                data = _snapshots[0].Payload;
            }
            return (frame, data);
        }

        void TrimSnapshotsAfter(int frame)
        {
            while (_snapshots.Count > 0 && _snapshots[_snapshots.Count - 1].FixedFrame > frame)
            {
                _snapshots.RemoveAt(_snapshots.Count - 1);
            }
        }

        void TrimSnapshotsBefore(int frame)
        {
            int removeCount = 0;
            while (removeCount < _snapshots.Count && _snapshots[removeCount].FixedFrame < frame)
            {
                removeCount++;
            }
            if (removeCount > 0)
            {
                _snapshots.RemoveRange(0, removeCount);
            }
        }

        // Toggles the input-history-locker registration in lockstep with
        // _isRecording. Centralizes the add/remove pair so callers (Start,
        // Stop, Reset, LoadRecordingFromFile, Dispose) don't have to repeat
        // the world-disposed guard or worry about double-registration.
        void EnsureLockerRegistered(bool registered)
        {
            if (_lockerRegistered == registered)
            {
                return;
            }
            if (_world.IsDisposed || _accessor == null)
            {
                _lockerRegistered = false;
                return;
            }
            var queue = _accessor.GetEntityInputQueue();
            if (registered)
            {
                queue.AddHistoryLocker(this);
            }
            else
            {
                queue.RemoveHistoryLocker(this);
            }
            _lockerRegistered = registered;
        }

        void TrimAnchorsAfter(int frame)
        {
            while (_anchors.Count > 0 && _anchors[_anchors.Count - 1].FixedFrame > frame)
            {
                var last = _anchors[_anchors.Count - 1];
                _totalBytes -= last.Payload.Length;
                _anchors.RemoveAt(_anchors.Count - 1);
            }
            // The anchor cadence tracker resets to the new tail (or sentinel
            // if no anchors remain) so the next due-check measures from the
            // right point.
            _lastAnchorFrame =
                _anchors.Count > 0
                    ? _anchors[_anchors.Count - 1].FixedFrame
                    : _startFrame - int.MaxValue / 2;
        }

        // Scrub cache is transient — drop everything past `frame`. Reset the
        // cadence tracker similarly so subsequent live captures don't think
        // they just made one.
        void TrimScrubCacheAfter(int frame)
        {
            while (_scrubCache.Count > 0 && _scrubCache[_scrubCache.Count - 1].FixedFrame > frame)
            {
                var last = _scrubCache[_scrubCache.Count - 1];
                _scrubCacheBytes -= last.Payload.Length;
                _scrubCache.RemoveAt(_scrubCache.Count - 1);
            }
            _lastScrubCacheFrame =
                _scrubCache.Count > 0
                    ? _scrubCache[_scrubCache.Count - 1].FixedFrame
                    : _startFrame - int.MaxValue / 2;
        }

        void TrimScrubCacheBefore(int frame)
        {
            int removeCount = 0;
            while (removeCount < _scrubCache.Count && _scrubCache[removeCount].FixedFrame < frame)
            {
                _scrubCacheBytes -= _scrubCache[removeCount].Payload.Length;
                removeCount++;
            }
            if (removeCount > 0)
            {
                _scrubCache.RemoveRange(0, removeCount);
            }
        }

#if !TRECS_IS_PROFILING
        // Drop per-frame checksums whose frame is strictly greater than
        // `frame`. Mirrors TrimAnchorsAfter / TrimScrubCacheAfter for the
        // checksum dict. DenseDictionary doesn't support remove-during-
        // enumeration, so the keys to drop are collected first.
        void TrimChecksumsAfter(int frame)
        {
            if (_checksums.IsEmpty)
            {
                return;
            }
            List<int> toRemove = null;
            foreach (var (k, _) in _checksums)
            {
                if (k > frame)
                {
                    toRemove ??= new List<int>();
                    toRemove.Add(k);
                }
            }
            if (toRemove == null)
            {
                return;
            }
            foreach (var k in toRemove)
            {
                _checksums.TryRemove(k);
            }
        }

        // Drop per-frame checksums whose frame is strictly less than `frame`.
        void TrimChecksumsBefore(int frame)
        {
            if (_checksums.IsEmpty)
            {
                return;
            }
            List<int> toRemove = null;
            foreach (var (k, _) in _checksums)
            {
                if (k < frame)
                {
                    toRemove ??= new List<int>();
                    toRemove.Add(k);
                }
            }
            if (toRemove == null)
            {
                return;
            }
            foreach (var k in toRemove)
            {
                _checksums.TryRemove(k);
            }
        }

        DenseDictionary<int, uint> CopyChecksums()
        {
            var copy = new DenseDictionary<int, uint>();
            foreach (var (frame, checksum) in _checksums)
            {
                copy.Add(frame, checksum);
            }
            return copy;
        }
#else
        // Stubs so non-profiling-guarded callers compile in profiling builds.
        // No-op trims and an empty bundle dict — runtime behaviour matches
        // the current "Checksums in saved bundle is empty" state under
        // TRECS_IS_PROFILING, which is the correct trade-off (no per-frame
        // hash work in profiling builds).
        void TrimChecksumsAfter(int frame) { }

        void TrimChecksumsBefore(int frame) { }

        DenseDictionary<int, uint> CopyChecksums() => new DenseDictionary<int, uint>();
#endif

        // Subscribed via Events.OnFixedUpdateCompleted so capture happens AFTER
        // `_elapsedFixedTime += step`, keeping (frame, elapsed) in lockstep.
        // FixedFrameChangeEvent (the obvious-looking alternative) fires between
        // counter++ and elapsed+=step, so a snapshot captured there would store
        // elapsed one tick behind the frame counter — and after LoadSnapshot the
        // resimulation could never reproduce that off-by-one state, leading to a
        // checksum mismatch on the very first verified snapshot.
        void OnFixedFrameChange()
        {
            var frame = _accessor.FixedFrame;
            // Skip everything during the FF kicked off by JumpToFrame: we don't
            // want to capture mid-FF snapshots and we don't want the truncation
            // logic to interpret FF advancement as user-driven progression.
            // When the FF reaches its target, the user has stably scrubbed to
            // that frame — snapshots past it are tentatively stale (auto-rec)
            // or just preserved playback content (loaded).
            if (_fastForwardTargetFrame.HasValue)
            {
                // FF *is* the simulation re-running through a previously
                // recorded range, so it's just as valid a place to detect
                // desyncs as the post-FF Playback walk. Verifying here lets
                // a single click-and-jump catch divergence anywhere in the
                // skipped range — without this, only frames the user
                // explicitly Plays through afterwards would be checked.
                VerifyChecksumAtSnapshotFrame(frame);
                if (frame >= _fastForwardTargetFrame.Value)
                {
                    _pendingDivergenceFrame = frame;
                    _fastForwardTargetFrame = null;
                }
                return;
            }

            if (_isLoadedRecording)
            {
                // Loaded recordings: don't truncate trailing snapshots (they
                // are the recording's content). Clear divergence on first
                // forward step so the controller's mode flips to Recording-
                // adjacent (still IsLoadedRecording though).
                if (_pendingDivergenceFrame.HasValue && frame > _pendingDivergenceFrame.Value)
                {
                    _pendingDivergenceFrame = null;
                }
                if (frame < LastAnchorFrame)
                {
                    // Still inside the loaded buffer — let it play through.
                    VerifyChecksumAtSnapshotFrame(frame);
                    return;
                }
                if (frame == LastAnchorFrame)
                {
                    // First arrival at the loaded buffer's tail. Verify the
                    // tail snapshot first — if a desync occurred between the
                    // previous snapshot and here, this is the user's last
                    // chance to catch it within this buffer.
                    VerifyChecksumAtSnapshotFrame(frame);
                    if (_world.IsDisposed)
                    {
                        return;
                    }
                    if (_isLoopingPlayback)
                    {
                        BeginLoopRewind();
                        return;
                    }
                    // Default: stop so the user notices we've reached the
                    // end. From here the user can scrub back, click Record
                    // (Fork) to commit + go live, or press Play to promote
                    // past the tail.
                    _accessor.GetSystemRunner().FixedIsPaused = true;
                    return;
                }
                // frame > tail: user explicitly resumed past the
                // recording's tail. Promote to regular auto-recording from
                // here so capture continues live without a pause-loop.
                _isLoadedRecording = false;
                DropAbandonedTimelineInputs();
                CaptureSnapshotIfDue();
                return;
            }

            // Pending divergence means the user scrubbed back into the
            // buffer; pressing Play walks forward through the existing
            // snapshots rather than overwriting them. At the tail, three
            // possible actions:
            //   * Loop on  → rewind to the start, keep playing.
            //   * Loop off → pause. Symmetric with the loaded-recording
            //                branch above; the user has to press Record
            //                (which Forks during Playback) to commit and
            //                go live. Auto-promoting at the tail used to
            //                be the default here, but that's exactly what
            //                the explicit Record/Fork action does, so
            //                making it implicit just confused the
            //                interaction with the Loop toggle.
            if (_pendingDivergenceFrame.HasValue)
            {
                if (frame < LastAnchorFrame)
                {
                    // Still inside the buffer — let it play through.
                    // Don't capture (we already have a snapshot for this
                    // region) and don't trim.
                    VerifyChecksumAtSnapshotFrame(frame);
                    return;
                }
                // frame >= LastAnchorFrame: caught up to the live edge.
                // Verify the tail snapshot first (last chance to catch a
                // desync inside the existing buffer).
                VerifyChecksumAtSnapshotFrame(frame);
                if (_world.IsDisposed)
                {
                    return;
                }
                if (_isLoopingPlayback)
                {
                    BeginLoopRewind();
                    return;
                }
                // Pause at the tail. User picks the next action: scrub
                // back, press Record to Fork (commit + go live), or
                // toggle Loop on to repeat.
                _accessor.GetSystemRunner().FixedIsPaused = true;
                return;
            }

            CaptureSnapshotIfDue();
        }

        // Pause the runner now, then schedule a JumpToFrame(<start>) on
        // the next editor tick. Two reasons to defer via
        // EditorApplication.delayCall: (1) JumpToFrame disposes and
        // re-creates _frameSubscription, which is the very subscription
        // dispatching the OnFixedFrameChange call that triggered this; (2)
        // the current tick needs to unwind cleanly. *Critical*: the pause
        // must happen *before* scheduling — otherwise the next fixed
        // update fires before delayCall lands, advancing frame past
        // LastAnchorFrame and hitting the "promote past tail" branch,
        // which clears _isLoadedRecording / _pendingDivergenceFrame. By
        // the time delayCall runs, the recorder is in live capture and
        // the loop silently fails (the bug we shipped initially).
        void BeginLoopRewind()
        {
            // Loop target is the earliest scrubbable frame across anchors,
            // snapshots, and the scrub cache — not _startFrame, which can
            // predate everything when the anchor cap has rolled. Called
            // only from tail branches, so at least one of the three lists
            // is non-empty.
            _accessor.GetSystemRunner().FixedIsPaused = true;
            var loopTarget = FindEarliestPersisted().frame;
            EditorApplication.delayCall += () =>
            {
                if (!_isRecording || _world.IsDisposed)
                {
                    return;
                }
                if (JumpToFrame(loopTarget))
                {
                    // JumpToFrame pauses on a backward seek; un-pause so
                    // the loop keeps playing.
                    _accessor.GetSystemRunner().FixedIsPaused = false;
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
            if (_world.IsDisposed)
            {
                return;
            }
            _accessor.GetEntityInputQueue().ClearFutureInputsAfterOrAt(_accessor.FixedFrame);
        }

        // Three independent cadences fire from this single tick handler:
        // anchors (sparse, persisted), scrub-cache (dense, transient), and
        // per-frame checksums (dense, persisted into the saved bundle for
        // playback desync detection). When the world-state hash is needed by
        // multiple cadences on the same frame it's computed once and shared.
        // When both anchor and scrub are due the snapshot bytes go to the
        // anchor list; the entry there serves both navigation roles, so
        // adding it to the scrub cache too would just duplicate bytes for
        // no win.
        void CaptureSnapshotIfDue()
        {
            if (_world.IsDisposed)
            {
                return;
            }

            var fixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
            var now = _accessor.FixedFrame;
            var anchorElapsed = (now - _lastAnchorFrame) * fixedDeltaTime;
            var scrubElapsed = (now - _lastScrubCacheFrame) * fixedDeltaTime;

            var anchorDue = anchorElapsed >= _settings.AnchorIntervalSeconds;
            var scrubDue = scrubElapsed >= _settings.ScrubCacheIntervalSeconds;
#if !TRECS_IS_PROFILING
            // Per-frame checksum cadence — sparse compared to fixed frames,
            // dense compared to anchors. Catches desyncs close to where they
            // happen during BundlePlayer playback. Mirrors BundleRecorder so
            // editor-saved bundles have the same coverage runtime-saved ones
            // do. Skipped under TRECS_IS_PROFILING so editor profiling builds
            // don't pay the per-frame hash cost.
            var checksumDue = (now - _startFrame) % _settings.ChecksumFrameInterval == 0;
#else
            var checksumDue = false;
#endif
            if (!anchorDue && !scrubDue && !checksumDue)
            {
                return;
            }

            // Compute the world-state checksum once and share across the
            // cadences that need it. Snapshot bytes themselves include
            // type-check tags and other transient framing, so we use the
            // IsForChecksum-flavored serialization which strips those.
            var checksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _settings.Version,
                _checksumBuffer,
                SerializationFlags.IsForChecksum
            );

#if !TRECS_IS_PROFILING
            if (checksumDue)
            {
                // Overwrite rather than Add: scrubbed-back-then-resumed
                // recordings re-walk previously recorded frames during the
                // playback portion (before reaching the live edge), and a
                // subsequent fork can land at any frame; we want the most
                // recent live-capture value.
                _checksums[now] = checksum;
            }
#endif

            if (!anchorDue && !scrubDue)
            {
                // Per-frame checksum was the only thing due — no snapshot
                // bytes to capture this tick.
                return;
            }

            using var stream = new MemoryStream();
            var metadata = _snapshotSerializer.SaveSnapshot(
                _settings.Version,
                stream,
                includeTypeChecks: true
            );
            var data = stream.ToArray();
            // The same checksum is stored alongside the anchor so a future
            // re-run of this frame (after JumpToFrame) can verify the world
            // reached the same state.
            var anchor = new BundleAnchor
            {
                FixedFrame = metadata.FixedFrame,
                Payload = data,
                Checksum = checksum,
            };

            if (anchorDue)
            {
                _anchors.Add(anchor);
                _lastAnchorFrame = metadata.FixedFrame;
                // Anchor counts as a scrub-cache update too — the entry is
                // searched alongside the cache during JumpToFrame.
                _lastScrubCacheFrame = metadata.FixedFrame;
                _totalBytes += data.Length;
            }
            else
            {
                _scrubCache.Add(anchor);
                _lastScrubCacheFrame = metadata.FixedFrame;
                _scrubCacheBytes += data.Length;
            }

#if ENABLE_DESYNC_DEBUGGING
            OutputJsonState($"recording_snapshot{metadata.FixedFrame}.json");
#endif

            EnforceCapacityLimits();
        }

#if ENABLE_DESYNC_DEBUGGING
        // Dump the current world state to a JSON file for desync diffing.
        // Uses the same IWorldStateSerializer + IsForChecksum flag the
        // checksum calculator uses, so the JSON capture covers exactly the
        // field set the desync detection compares — diffing two files
        // reveals which field drifted.
        void OutputJsonState(string outputFileName)
        {
            using var fileStream = File.Create(Path.Combine(SnapshotsDirName, outputFileName));
            using var streamWriter = new StreamWriter(fileStream);
            using var jsonWriter = new JsonTextWriter(streamWriter);
            jsonWriter.Formatting = Formatting.Indented;

            var writer = new JsonSerializationWriter(jsonWriter, _serializerRegistry);
            writer.Start(version: _settings.Version, flags: SerializationFlags.IsForChecksum);
            _stateSerializer.SerializeState(writer);
            writer.Complete();
        }
#endif

        // Verify the simulation produced the same world state we captured
        // at this frame. Called from OnFixedFrameChange while the user is
        // walking forward inside the buffer (Playback mode). Sets the desync
        // marker on first mismatch and stops checking — once desynced the
        // same buffer can't trust further checksums anyway.
        void VerifyChecksumAtSnapshotFrame(int frame)
        {
            if (_desyncedFrame.HasValue || _world.IsDisposed)
            {
                return;
            }
            // Anchors are sparse (every AnchorIntervalSeconds); a binary
            // search would be cleaner but the list is small (capped) so a
            // tail-walk is fine and matches the rest of this file's style.
            for (int i = _anchors.Count - 1; i >= 0; i--)
            {
                var bm = _anchors[i];
                if (bm.FixedFrame == frame)
                {
                    // Checksum 0 is the sentinel for "missing" — used by
                    // anchors loaded from a v2 file (pre-checksum format)
                    // or for the rare actual hash collision with 0. Skip
                    // verification rather than report a phantom desync.
                    if (bm.Checksum == 0u)
                    {
                        return;
                    }
                    var actual = _checksumCalculator.CalculateCurrentChecksum(
                        version: _settings.Version,
                        _checksumBuffer,
                        SerializationFlags.IsForChecksum
                    );
#if ENABLE_DESYNC_DEBUGGING
                    OutputJsonState($"playback_snapshot{frame}.json");
#endif
                    if (actual != bm.Checksum)
                    {
                        _desyncedFrame = frame;
                        _log.Warning(
                            "Desync at frame {}: expected checksum {} but got {} "
                                + "(simulation re-run from an earlier anchor produced "
                                + "different state — non-determinism in your code or data).",
                            frame,
                            bm.Checksum,
                            actual
                        );
                    }
                    return;
                }
                if (bm.FixedFrame < frame)
                {
                    return;
                }
            }
        }

        // Both stores cap by drop-oldest. Anchors are sparse so they're
        // capped by count; scrub cache is dense so it's capped by bytes.
        // Snapshots are user-controlled and never auto-evicted.
        void EnforceCapacityLimits()
        {
            var maxAnchors = _settings.MaxAnchorCount;
            if (maxAnchors > 0)
            {
                while (_anchors.Count > maxAnchors)
                {
                    var oldest = _anchors[0];
                    _totalBytes -= oldest.Payload.Length;
                    _anchors.RemoveAt(0);
                }
                if (_anchors.Count > 0)
                {
                    // The recording's earliest scrubbable frame moved
                    // forward; track that and prune queued inputs that
                    // predate it. Snapshots before the new earliest are
                    // intentionally kept — the user placed them deliberately
                    // and they're still scrubbable on their own.
                    _startFrame = _anchors[0].FixedFrame;
                    if (!_world.IsDisposed)
                    {
                        _accessor
                            .GetEntityInputQueue()
                            .ClearInputsBeforeOrAt(MaxClearFrame ?? _startFrame - 1);
                    }
                }
            }

            var maxScrubBytes = _settings.MaxScrubCacheBytes;
            if (maxScrubBytes > 0)
            {
                while (_scrubCache.Count > 0 && _scrubCacheBytes > maxScrubBytes)
                {
                    var oldest = _scrubCache[0];
                    _scrubCacheBytes -= oldest.Payload.Length;
                    _scrubCache.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>
    /// Header summary parsed from a saved recording file. Exposed so editor
    /// tooling can inspect frame span / tick rate without loading the full
    /// snapshot list. See <see cref="TrecsAutoRecorder.TryReadRecordingHeader"/>.
    /// </summary>
    public readonly struct RecordingHeader
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
