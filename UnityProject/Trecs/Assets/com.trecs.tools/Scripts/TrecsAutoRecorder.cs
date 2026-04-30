// #define ENABLE_DESYNC_DEBUGGING

using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;
using Trecs.Serialization;
using UnityEngine;
#if ENABLE_DESYNC_DEBUGGING
using Newtonsoft.Json;
#endif

namespace Trecs.Tools
{
    /// <summary>
    /// Periodically captures full-state bookmarks of a Trecs <see cref="World"/>
    /// while running. Bookmarks are kept in memory only — no disk I/O. Designed
    /// to be enabled on demand by editor tooling (see <c>TrecsTimeTravelWindow</c>).
    /// </summary>
    public class TrecsAutoRecorder : IDisposable, IInputHistoryLocker
    {
        // v3: each bookmark also stores a uint checksum of its frame's world
        // state (computed via IWorldStateSerializer with IsForChecksum) so
        // we can detect desyncs when the simulation re-runs the same frame.
        // v2 files are still accepted but lack checksums — desync detection
        // is silently disabled for those buffers.
        const int RecordingFileVersion = 3;
        const int RecordingFileVersionLegacyV2 = 2;
        const int RecordingFileMagic = 0x43455254; // "TREC" little-endian
#if ENABLE_DESYNC_DEBUGGING
        // JSON snapshots for diff-driven desync diagnosis. One file per
        // captured live bookmark and one per replay verification, in the
        // same directory the old DebugRecordingHandler used so the diff
        // workflow is identical (`diff recording_snapshot<frame>.json
        // playback_snapshot<frame>.json` after the first reported desync).
        const string SnapshotsDirName = SvkjDebugConstants.TempDirName + "/snapshots";
#endif

        static readonly TrecsLog _log = new(nameof(TrecsAutoRecorder));

        readonly World _world;
        readonly IWorldStateSerializer _stateSerializer;
        readonly SerializerRegistry _serializerRegistry;
        readonly TrecsAutoRecorderSettings _settings;
        readonly List<AutoRecordingBookmark> _bookmarks = new();

        WorldAccessor _accessor;
        BookmarkSerializer _bookmarkSerializer;
        RecordingChecksumCalculator _checksumCalculator;
        SerializationBuffer _checksumBuffer;
        IDisposable _frameSubscription;

        bool _isRecording;
        int _startFrame;
        int _lastBookmarkFrame;
        long _totalBytes;

        // The frame the user has most-recently scrubbed to (post-fast-forward
        // if applicable). While set, any bookmarks past this frame are
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

        bool _isPausedByCapacity;

        // True iff the current in-memory buffer was loaded from a saved
        // recording file (LoadRecordingFromFile). While set:
        //   * Trailing bookmarks past the divergence point are NOT trimmed
        //     when simulation advances — the loaded recording's "future" is
        //     preserved so the user can scrub it again.
        //   * When the simulation reaches the last loaded bookmark, fixed
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
            TrecsAutoRecorderSettings settings
        )
        {
            _world = world;
            _stateSerializer = stateSerializer;
            _serializerRegistry = serializerRegistry;
            _settings = settings;
        }

        public World World => _world;
        public bool IsRecording => _isRecording;
        public int StartFrame => _startFrame;
        public int LastBookmarkFrame => _lastBookmarkFrame;
        public IReadOnlyList<AutoRecordingBookmark> Bookmarks => _bookmarks;
        public long TotalBytes => _totalBytes;

        /// <summary>
        /// True when the world is at the recorder's live edge — i.e. there is
        /// no pending divergence from a prior scrub-back. Transitions to false
        /// when <see cref="JumpToFrame"/> rewinds the world; transitions back
        /// to true when the simulation advances past the divergence point with
        /// live input (truncating the trailing bookmarks). Drives the
        /// Recording vs Playback distinction in the controller.
        /// </summary>
        public bool IsAtLiveEdge =>
            !_pendingDivergenceFrame.HasValue && !_fastForwardTargetFrame.HasValue;

        /// <summary>
        /// Interval (in simulated seconds) between bookmark captures. Reads
        /// and writes <see cref="TrecsAutoRecorderSettings.BookmarkIntervalSeconds"/>
        /// directly so UI can tune it at runtime. Larger values save memory
        /// but slow scrubbing (more resim per JumpToFrame).
        /// </summary>
        public float BookmarkIntervalSeconds
        {
            get => _settings.BookmarkIntervalSeconds;
            set => _settings.BookmarkIntervalSeconds = Mathf.Max(0.001f, value);
        }

        /// <summary>True iff the most recent EnforceCapacityLimits pass paused
        /// the SystemRunner because the cap was hit. Cleared when the user
        /// resets, forks, or removes capacity pressure (e.g. raises the cap).
        /// </summary>
        public bool IsPausedByCapacity => _isPausedByCapacity;

        /// <summary>
        /// True iff the in-memory buffer came from a loaded recording file.
        /// Loaded buffers preserve their trailing bookmarks (the recording's
        /// future) when simulation advances, and auto-pause when the loaded
        /// buffer is exhausted. Cleared by Start/Reset/Fork.
        /// </summary>
        public bool IsLoadedRecording => _isLoadedRecording;

        /// <summary>Configured maximum bookmark count (0 = unbounded).</summary>
        public int MaxBookmarkCount
        {
            get => _settings.MaxBookmarkCount;
            set => _settings.MaxBookmarkCount = Mathf.Max(0, value);
        }

        /// <summary>Configured maximum bookmark byte budget (0 = unbounded).</summary>
        public long MaxBookmarkMemoryBytes
        {
            get => _settings.MaxBookmarkMemoryBytes;
            set => _settings.MaxBookmarkMemoryBytes = Math.Max(0, value);
        }

        /// <summary>What the recorder does when a capacity cap is reached.</summary>
        public CapacityOverflowAction OverflowAction
        {
            get => _settings.OverflowAction;
            set => _settings.OverflowAction = value;
        }

        /// <summary>
        /// Fractional fill of the tighter of the two caps, in [0, 1]. Returns
        /// 0 when both caps are unbounded (so callers can hide the meter).
        /// </summary>
        public float CapacityFraction
        {
            get
            {
                var byCount =
                    _settings.MaxBookmarkCount > 0
                        ? _bookmarks.Count / (float)_settings.MaxBookmarkCount
                        : 0f;
                var byBytes =
                    _settings.MaxBookmarkMemoryBytes > 0
                        ? _totalBytes / (float)_settings.MaxBookmarkMemoryBytes
                        : 0f;
                return Mathf.Clamp01(Mathf.Max(byCount, byBytes));
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
        /// bookmark and produced a state whose checksum did not match the
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
                // Use the earliest scrubbable frame: the first bookmark, or
                // (pre-first-bookmark) the recording start frame. Inputs at
                // frames < this are no longer needed.
                var earliest = _bookmarks.Count > 0 ? _bookmarks[0].Frame : _startFrame;
                return earliest - 1;
            }
        }

        public void Initialize()
        {
            _accessor = _world.CreateAccessor("TrecsAutoRecorder");
            _bookmarkSerializer = new BookmarkSerializer(
                _stateSerializer,
                _serializerRegistry,
                _world
            );
            _checksumCalculator = new RecordingChecksumCalculator(_stateSerializer);
            _checksumBuffer = new SerializationBuffer(_serializerRegistry);
        }

        public void Dispose()
        {
            Stop();
            _bookmarkSerializer?.Dispose();
            _bookmarkSerializer = null;
            _checksumBuffer?.Dispose();
            _checksumBuffer = null;
        }

        public void Start()
        {
            if (_isRecording)
            {
                return;
            }

            _bookmarks.Clear();
            _totalBytes = 0;
            _startFrame = _accessor.FixedFrame;
            // Force the first FixedUpdateCompleted tick to capture immediately.
            // We don't capture here in Start() because the activator may invoke
            // us during Layer.Initialize, before downstream serializers (e.g.
            // Orca's LuaStateSerializer) have finished their own init.
            _lastBookmarkFrame = _startFrame - int.MaxValue / 2;
            _pendingDivergenceFrame = null;
            _fastForwardTargetFrame = null;
            _isPausedByCapacity = false;
            _isLoadedRecording = false;
            _desyncedFrame = null;
            _isRecording = true;

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
                "Auto recording stopped — captured {} bookmarks ({} bytes total)",
                _bookmarks.Count,
                _totalBytes
            );
        }

        /// <summary>
        /// Restore the world state to <paramref name="targetFrame"/> by loading
        /// the latest bookmark whose frame is <c>&lt;= targetFrame</c>, and (if
        /// needed) fast-forwarding the simulation up to <paramref name="targetFrame"/>.
        /// Bookmarks past <paramref name="targetFrame"/> are kept (tentatively
        /// stale) so the user can keep scrubbing forward and back while paused;
        /// they get truncated only when the simulation actually progresses past
        /// the load point with live (user-driven) input. The world is left
        /// fixed-paused at the target so the user can inspect.
        /// Returns false if there is no bookmark at or before <paramref name="targetFrame"/>.
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

            var bookmarkIdx = FindBookmarkIndexAtOrBefore(targetFrame);
            if (bookmarkIdx < 0)
            {
                if (_bookmarks.Count == 0)
                {
                    _log.Warning("No bookmarks recorded yet — cannot jump");
                    return false;
                }
                // Target precedes the earliest bookmark. This typically happens
                // for the leftmost slider position or jump-to-start: StartFrame
                // is the frame recording started at, but the first bookmark
                // arrives one frame later. Snap up to the first bookmark —
                // it's the earliest scrubbable frame in the buffer.
                bookmarkIdx = 0;
                targetFrame = _bookmarks[0].Frame;
            }

            var bookmark = _bookmarks[bookmarkIdx];
            var runner = _accessor.GetSystemRunner();

            // Detach from frame events so we don't capture a "snapshot" of the
            // half-restored state during the load itself.
            _frameSubscription?.Dispose();
            _frameSubscription = null;

            try
            {
                using (var stream = new MemoryStream(bookmark.Data, writable: false))
                {
                    _bookmarkSerializer.LoadBookmark(stream);
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
                    // scrub position. Bookmarks past it are tentatively stale.
                    _fastForwardTargetFrame = null;
                    _pendingDivergenceFrame = _accessor.FixedFrame;
                    runner.FixedIsPaused = true;
                }

                // Each new scrub gets a fresh chance to verify checksums.
                // The previous desync (if any) was tied to the prior walk;
                // clear it so the user can scrub past the suspected frame
                // and watch for it again, or jump elsewhere and observe.
                _desyncedFrame = null;

                // Re-subscribe so further frames continue to bookmark normally.
                _frameSubscription = _accessor.Events.OnFixedUpdateCompleted(OnFixedFrameChange);
            }
            catch (Exception e)
            {
                // World state may be partially loaded — keep recording running
                // would silently capture corrupt snapshots. Stop cleanly so the
                // user sees auto-recording as inactive and can decide to
                // restart.
                _log.Error("JumpToFrame to bookmark @ frame {} failed: {}", bookmark.Frame, e);
                _isRecording = false;
                _fastForwardTargetFrame = null;
                _pendingDivergenceFrame = null;
                return false;
            }

            _log.Debug("Jumped to frame {} via bookmark at frame {}", targetFrame, bookmark.Frame);
            return true;
        }

        /// <summary>
        /// Persist the current in-memory bookmark list, plus the live
        /// EntityInputQueue covering its frame range, to <paramref name="filePath"/>.
        /// The recorder must currently be running and have at least one
        /// bookmark.
        /// </summary>
        public bool SaveRecordingToFile(string filePath)
        {
            if (!_isRecording)
            {
                _log.Warning("SaveRecordingToFile called while not recording");
                return false;
            }
            if (_bookmarks.Count == 0)
            {
                _log.Warning("SaveRecordingToFile: no bookmarks to save");
                return false;
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // RecordingMetadata-style header. start/end frame span the
            // first/last bookmarks; fixedDeltaTime lets the loader detect
            // tick-rate mismatches before applying inputs to a freshly
            // started world. Blob IDs are recorded so a loader could (in
            // future) verify the heap has them, though we don't enforce
            // that yet.
            var endFrame = _bookmarks[_bookmarks.Count - 1].Frame;
            var fixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
            var blobs = new DenseHashSet<BlobId>();
            _world.GetBlobCache().GetAllActiveBlobIds(blobs);

            // Serialize the input queue first into a side buffer so we can
            // write its byte length up-front. The queue uses Trecs's own
            // versioned binary format with type-checks; we keep that as an
            // opaque blob within our outer file format.
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

            using var fs = File.Create(filePath);
            using var bw = new BinaryWriter(fs);
            bw.Write(RecordingFileMagic);
            bw.Write(RecordingFileVersion);

            // Header: schema version + frame span + tick rate.
            bw.Write(_settings.Version);
            bw.Write(_startFrame);
            bw.Write(endFrame);
            bw.Write(fixedDeltaTime);

            // Blob-ID set referenced by recorded state.
            bw.Write(blobs.Count);
            foreach (var blob in blobs)
            {
                bw.Write(blob.Value);
            }

            // Bookmarks. v3 adds a per-bookmark checksum so loaded recordings
            // can also surface desyncs during playback.
            bw.Write(_bookmarks.Count);
            foreach (var b in _bookmarks)
            {
                bw.Write(b.Frame);
                bw.Write(b.Checksum);
                bw.Write(b.Data.Length);
                bw.Write(b.Data);
            }

            // EntityInputQueue payload.
            bw.Write(queueBytes.Length);
            bw.Write(queueBytes);

            _log.Debug(
                "Saved recording: {} bookmarks, {} blob refs, {} bytes input queue",
                _bookmarks.Count,
                blobs.Count,
                queueBytes.Length
            );
            return true;
        }

        /// <summary>
        /// Replace the in-memory bookmark list with one read from
        /// <paramref name="filePath"/>, restore world state to the earliest
        /// loaded bookmark, and leave the world fixed-paused there. Re-attaches
        /// the FixedUpdateCompleted subscription so further bookmarks will be
        /// captured when the user steps or unpauses.
        /// </summary>
        public bool LoadRecordingFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                _log.Warning("Recording file does not exist: {}", filePath);
                return false;
            }

            int startFrame;
            List<AutoRecordingBookmark> loadedBookmarks;
            byte[] queueBytes;
            try
            {
                using var fs = File.OpenRead(filePath);
                using var br = new BinaryReader(fs);
                var magic = br.ReadInt32();
                if (magic != RecordingFileMagic)
                {
                    _log.Error(
                        "Bad recording file header (magic={}); expected {}",
                        magic,
                        RecordingFileMagic
                    );
                    return false;
                }
                var version = br.ReadInt32();
                if (version != RecordingFileVersion && version != RecordingFileVersionLegacyV2)
                {
                    _log.Error(
                        "Unsupported recording file version: {} (expected {} or legacy {}). "
                            + "Recordings saved before the input-queue format change can no longer be loaded.",
                        version,
                        RecordingFileVersion,
                        RecordingFileVersionLegacyV2
                    );
                    return false;
                }
                var hasChecksums = version >= RecordingFileVersion;
                var schemaVersion = br.ReadInt32();
                if (schemaVersion != _settings.Version)
                {
                    _log.Warning(
                        "Recording schema version {} does not match current {} — "
                            + "load may fail or the simulation may desync",
                        schemaVersion,
                        _settings.Version
                    );
                }
                startFrame = br.ReadInt32();
                br.ReadInt32(); // endFrame — informational; recomputed from loaded bookmarks.
                var fixedDeltaTime = br.ReadSingle();
                var currentFixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
                if (!Mathf.Approximately(fixedDeltaTime, currentFixedDeltaTime))
                {
                    _log.Warning(
                        "Recording fixed delta time {} differs from current {} — input replay may desync",
                        fixedDeltaTime,
                        currentFixedDeltaTime
                    );
                }
                var blobCount = br.ReadInt32();
                for (int i = 0; i < blobCount; i++)
                {
                    // Blob IDs are recorded for future verification (heap
                    // membership before applying state). Skip-read for now —
                    // we'd rather warn-and-load than block on a missing blob
                    // since the user is debugging by definition.
                    br.ReadInt64();
                }

                var count = br.ReadInt32();
                loadedBookmarks = new List<AutoRecordingBookmark>(count);
                for (int i = 0; i < count; i++)
                {
                    var frame = br.ReadInt32();
                    var checksum = hasChecksums ? br.ReadUInt32() : 0u;
                    var len = br.ReadInt32();
                    var data = br.ReadBytes(len);
                    loadedBookmarks.Add(new AutoRecordingBookmark(frame, data, checksum));
                }

                var queueLen = br.ReadInt32();
                queueBytes = br.ReadBytes(queueLen);
            }
            catch (Exception e)
            {
                _log.Error("Failed to read recording from {}: {}", filePath, e);
                return false;
            }

            if (loadedBookmarks.Count == 0)
            {
                _log.Warning("Loaded recording has no bookmarks");
                return false;
            }

            // Detach the subscription for the duration of the load.
            _frameSubscription?.Dispose();
            _frameSubscription = null;

            try
            {
                // Restore world state to the earliest loaded bookmark.
                var earliest = loadedBookmarks[0];
                using (var stream = new MemoryStream(earliest.Data, writable: false))
                {
                    _bookmarkSerializer.LoadBookmark(stream);
                }

                // Wipe the live queue and replace it with the recording's
                // serialized inputs. ClearAllInputs (vs. ClearFutureInputsAfterOrAt)
                // is correct here: we're switching timelines wholesale, and any
                // pre-loaded-frame inputs on the live timeline don't apply.
                var inputQueue = _accessor.GetEntityInputQueue();
                inputQueue.ClearAllInputs();
                if (queueBytes.Length > 0)
                {
                    using var queueBuffer = new SerializationBuffer(_serializerRegistry);
                    queueBuffer.MemoryStream.Write(queueBytes, 0, queueBytes.Length);
                    queueBuffer.MemoryStream.Position = 0;
                    queueBuffer.StartRead();
                    inputQueue.Deserialize(new TrecsSerializationReaderAdapter(queueBuffer));
                    queueBuffer.StopRead(verifySentinel: false);
                }

                // Replace in-memory state with the loaded recording.
                _bookmarks.Clear();
                _bookmarks.AddRange(loadedBookmarks);
                _totalBytes = 0;
                foreach (var b in _bookmarks)
                {
                    _totalBytes += b.Data.LongLength;
                }
                _startFrame = startFrame;
                _lastBookmarkFrame = _bookmarks[_bookmarks.Count - 1].Frame;
                _pendingDivergenceFrame = earliest.Frame;
                _fastForwardTargetFrame = null;
                _isPausedByCapacity = false;
                _isLoadedRecording = true;
                _desyncedFrame = null;
                _isRecording = true;

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
                _bookmarks.Clear();
                _totalBytes = 0;
                _isRecording = false;
                _fastForwardTargetFrame = null;
                _pendingDivergenceFrame = null;
                _isLoadedRecording = false;
                _desyncedFrame = null;
                EnsureLockerRegistered(false);
                return false;
            }

            _log.Debug(
                "Loaded recording with {} bookmarks (frames {} .. {})",
                _bookmarks.Count,
                _bookmarks[0].Frame,
                _bookmarks[_bookmarks.Count - 1].Frame
            );
            return true;
        }

        /// <summary>
        /// Truncate any bookmarks past the world's current frame and clear
        /// pending divergence so the recorder is at the live edge again.
        /// Used by the controller's "Fork &amp; resume" gesture: the user has
        /// scrubbed into Playback, decides to commit at this point, and wants
        /// new live input to extend the buffer from here. The recorder must
        /// currently be running and have at least one bookmark at or before
        /// the current frame.
        /// </summary>
        public bool ForkAtCurrentFrame()
        {
            if (!_isRecording || _world.IsDisposed)
            {
                return false;
            }
            var current = _accessor.FixedFrame;
            if (FindBookmarkIndexAtOrBefore(current) < 0)
            {
                _log.Warning(
                    "ForkAtCurrentFrame: no bookmark at or before frame {} — cannot fork",
                    current
                );
                return false;
            }
            TrimBookmarksAfter(current);
            DropAbandonedTimelineInputs();
            _pendingDivergenceFrame = null;
            _isPausedByCapacity = false;
            _isLoadedRecording = false;
            _desyncedFrame = null;
            _log.Debug("Forked recording at frame {}; trailing bookmarks dropped", current);
            return true;
        }

        /// <summary>
        /// Drop bookmarks whose frame is strictly less than <paramref name="frame"/>,
        /// preserving the closest bookmark at-or-before <paramref name="frame"/> (so
        /// <see cref="JumpToFrame"/> at the trim point still works). The new earliest
        /// bookmark becomes the recording's start frame; queued inputs prior to it
        /// are pruned in lockstep. Returns the count of dropped bookmarks (0 if no
        /// trim happened — recorder not running, no candidates to drop, or the trim
        /// point is at-or-before the current earliest bookmark).
        /// </summary>
        public int TrimRecordingBefore(int frame)
        {
            if (!_isRecording)
            {
                return 0;
            }
            var keepFrom = FindBookmarkIndexAtOrBefore(frame);
            if (keepFrom <= 0)
            {
                return 0;
            }
            long droppedBytes = 0;
            for (var i = 0; i < keepFrom; i++)
            {
                droppedBytes += _bookmarks[i].Data.Length;
            }
            _bookmarks.RemoveRange(0, keepFrom);
            _totalBytes -= droppedBytes;
            _startFrame = _bookmarks[0].Frame;
            if (!_world.IsDisposed)
            {
                _accessor.GetEntityInputQueue().ClearInputsBeforeOrAt(_startFrame - 1);
            }
            _log.Debug(
                "Trimmed {} bookmarks before frame {} ({} bytes dropped)",
                keepFrom,
                frame,
                droppedBytes
            );
            return keepFrom;
        }

        /// <summary>
        /// Drop bookmarks whose frame is strictly greater than <paramref name="frame"/>.
        /// Other recorder state (divergence, loaded-recording flag, desync) is
        /// intentionally preserved — this is a pure data trim, distinct from
        /// <see cref="ForkAtCurrentFrame"/> which also commits the current scrub
        /// frame as the new live edge. Returns the count of dropped bookmarks.
        /// </summary>
        public int TrimRecordingAfter(int frame)
        {
            if (!_isRecording)
            {
                return 0;
            }
            var countBefore = _bookmarks.Count;
            var bytesBefore = _totalBytes;
            TrimBookmarksAfter(frame);
            var dropped = countBefore - _bookmarks.Count;
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
                "Trimmed {} bookmarks after frame {} ({} bytes dropped)",
                dropped,
                frame,
                bytesBefore - _totalBytes
            );
            return dropped;
        }

        /// <summary>
        /// Discard all in-memory bookmark history and start a fresh recording
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
            _bookmarks.Clear();
            _totalBytes = 0;
            _startFrame = _accessor.FixedFrame;
            _lastBookmarkFrame = _startFrame - int.MaxValue / 2;
            _pendingDivergenceFrame = null;
            _fastForwardTargetFrame = null;
            _isPausedByCapacity = false;
            _isLoadedRecording = false;
            _desyncedFrame = null;
            DropAbandonedTimelineInputs();
            _log.Debug("Auto recording reset at frame {}", _startFrame);
        }

        /// <summary>
        /// Find the nearest bookmark whose frame is strictly less than the
        /// world's current frame and JumpToFrame to it. No-op if the recorder
        /// is not running or there is no such bookmark.
        /// </summary>
        public bool JumpToPreviousBookmark()
        {
            if (!_isRecording || _world.IsDisposed)
            {
                return false;
            }
            var current = _accessor.FixedFrame;
            for (int i = _bookmarks.Count - 1; i >= 0; i--)
            {
                if (_bookmarks[i].Frame < current)
                {
                    return JumpToFrame(_bookmarks[i].Frame);
                }
            }
            _log.Debug("No previous bookmark before frame {}", current);
            return false;
        }

        /// <summary>
        /// Find the nearest bookmark whose frame is strictly greater than the
        /// world's current frame and JumpToFrame to it. Useful while paused
        /// after a rewind, so the user can step through bookmarks.
        /// </summary>
        public bool JumpToNextBookmark()
        {
            if (!_isRecording || _world.IsDisposed)
            {
                return false;
            }
            var current = _accessor.FixedFrame;
            for (int i = 0; i < _bookmarks.Count; i++)
            {
                if (_bookmarks[i].Frame > current)
                {
                    return JumpToFrame(_bookmarks[i].Frame);
                }
            }
            _log.Debug("No next bookmark past frame {}", current);
            return false;
        }

        int FindBookmarkIndexAtOrBefore(int frame)
        {
            for (int i = _bookmarks.Count - 1; i >= 0; i--)
            {
                if (_bookmarks[i].Frame <= frame)
                {
                    return i;
                }
            }
            return -1;
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

        void TrimBookmarksAfter(int frame)
        {
            while (_bookmarks.Count > 0 && _bookmarks[_bookmarks.Count - 1].Frame > frame)
            {
                var last = _bookmarks[_bookmarks.Count - 1];
                _totalBytes -= last.Data.Length;
                _bookmarks.RemoveAt(_bookmarks.Count - 1);
            }
            _lastBookmarkFrame =
                _bookmarks.Count > 0
                    ? _bookmarks[_bookmarks.Count - 1].Frame
                    : _startFrame - int.MaxValue / 2;
        }

        // Subscribed via Events.OnFixedUpdateCompleted so capture happens AFTER
        // `_elapsedFixedTime += step`, keeping (frame, elapsed) in lockstep.
        // FixedFrameChangeEvent (the obvious-looking alternative) fires between
        // counter++ and elapsed+=step, so a bookmark captured there would store
        // elapsed one tick behind the frame counter — and after LoadBookmark the
        // resimulation could never reproduce that off-by-one state, leading to a
        // checksum mismatch on the very first verified bookmark.
        void OnFixedFrameChange()
        {
            var frame = _accessor.FixedFrame;
            // Skip everything during the FF kicked off by JumpToFrame: we don't
            // want to capture mid-FF snapshots and we don't want the truncation
            // logic to interpret FF advancement as user-driven progression.
            // When the FF reaches its target, the user has stably scrubbed to
            // that frame — bookmarks past it are tentatively stale (auto-rec)
            // or just preserved playback content (loaded).
            if (_fastForwardTargetFrame.HasValue)
            {
                // FF *is* the simulation re-running through a previously
                // recorded range, so it's just as valid a place to detect
                // desyncs as the post-FF Playback walk. Verifying here lets
                // a single click-and-jump catch divergence anywhere in the
                // skipped range — without this, only frames the user
                // explicitly Plays through afterwards would be checked.
                VerifyChecksumIfBookmarked(frame);
                if (frame >= _fastForwardTargetFrame.Value)
                {
                    _pendingDivergenceFrame = frame;
                    _fastForwardTargetFrame = null;
                }
                return;
            }

            if (_isLoadedRecording)
            {
                // Loaded recordings: don't truncate trailing bookmarks (they
                // are the recording's content). Clear divergence on first
                // forward step so the controller's mode flips to Recording-
                // adjacent (still IsLoadedRecording though).
                if (_pendingDivergenceFrame.HasValue && frame > _pendingDivergenceFrame.Value)
                {
                    _pendingDivergenceFrame = null;
                }
                if (frame < _lastBookmarkFrame)
                {
                    // Still inside the loaded buffer — let it play through.
                    VerifyChecksumIfBookmarked(frame);
                    return;
                }
                if (frame == _lastBookmarkFrame)
                {
                    // First arrival at the loaded buffer's tail. Verify the
                    // tail bookmark before pausing — if a desync occurred
                    // between the previous bookmark and here, this is the
                    // user's last chance to catch it within this buffer.
                    VerifyChecksumIfBookmarked(frame);
                    // Pause so the user notices we've reached the end and can
                    // decide to Fork (commit + continue) or scrub back. They
                    // can press Play again to push past — see below.
                    if (!_world.IsDisposed)
                    {
                        _accessor.GetSystemRunner().FixedIsPaused = true;
                    }
                    return;
                }
                // frame > _lastBookmarkFrame: user explicitly resumed past the
                // recording's tail. Promote to regular auto-recording from
                // here so capture continues live without a pause-loop.
                _isLoadedRecording = false;
                DropAbandonedTimelineInputs();
                CaptureBookmarkIfDue();
                return;
            }

            // Pending divergence means the user scrubbed back into the
            // buffer; pressing Play walks forward through the existing
            // bookmarks rather than overwriting them. The Fork button is
            // the explicit "commit + go live" action.
            // Unlike the loaded-recording branch above, we don't pause at
            // the tail — for auto-recording the tail IS the live edge, so
            // pausing there interrupts the user instead of marking a real
            // end-of-content. Just promote to live the moment we reach or
            // pass the tail.
            if (_pendingDivergenceFrame.HasValue)
            {
                if (frame < _lastBookmarkFrame)
                {
                    // Still inside the buffer — let it play through.
                    // Don't capture (we already have a bookmark for this
                    // region) and don't trim.
                    VerifyChecksumIfBookmarked(frame);
                    return;
                }
                // frame >= _lastBookmarkFrame: caught up to the live edge.
                // Verify the tail bookmark first (last chance to catch a
                // desync inside the existing buffer), then promote to live
                // recording — keep the existing buffer (no trim) and let
                // CaptureBookmarkIfDue resume.
                VerifyChecksumIfBookmarked(frame);
                _pendingDivergenceFrame = null;
                DropAbandonedTimelineInputs();
            }

            CaptureBookmarkIfDue();
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
        // no-FF branch pauses immediately after LoadBookmark. Either way
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

        void CaptureBookmarkIfDue()
        {
            if (_world.IsDisposed)
            {
                return;
            }

            var fixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
            var elapsedSinceLast = (_accessor.FixedFrame - _lastBookmarkFrame) * fixedDeltaTime;

            if (elapsedSinceLast < _settings.BookmarkIntervalSeconds)
            {
                return;
            }

            using var stream = new MemoryStream();
            var metadata = _bookmarkSerializer.SaveBookmark(
                _settings.Version,
                stream,
                includeTypeChecks: true
            );
            var data = stream.ToArray();
            // Capture a deterministic checksum alongside the bookmark so a
            // future re-run of this same frame (after JumpToFrame) can verify
            // the world reached the same state. Bookmark bytes themselves
            // include type-check tags and other transient framing, so we use
            // the IsForChecksum-flavored serialization which strips those.
            var checksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _settings.Version,
                _checksumBuffer,
                SerializationFlags.IsForChecksum
            );
            _bookmarks.Add(new AutoRecordingBookmark(metadata.FixedFrame, data, checksum));
            _lastBookmarkFrame = metadata.FixedFrame;
            _totalBytes += data.Length;

#if ENABLE_DESYNC_DEBUGGING
            OutputJsonState($"recording_snapshot{metadata.FixedFrame}.json");
#endif

            EnforceCapacityLimits();
        }

#if ENABLE_DESYNC_DEBUGGING
        // Mirrors DebugRecordingHandler.OutputJsonState. Uses the same
        // IWorldStateSerializer + IsForChecksum flag the checksum calculator
        // uses, so the JSON dump captures exactly the field set the desync
        // detection compares — diffing two files reveals which field drifted.
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
        void VerifyChecksumIfBookmarked(int frame)
        {
            if (_desyncedFrame.HasValue || _world.IsDisposed)
            {
                return;
            }
            // Bookmarks are sparse (every BookmarkIntervalSeconds); a binary
            // search would be cleaner but the list is small (capped) so a
            // tail-walk is fine and matches the rest of this file's style.
            for (int i = _bookmarks.Count - 1; i >= 0; i--)
            {
                var bm = _bookmarks[i];
                if (bm.Frame == frame)
                {
                    // Checksum 0 is the sentinel for "missing" — used by
                    // bookmarks loaded from a v2 file (pre-checksum format)
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
                                + "(simulation re-run from an earlier bookmark produced "
                                + "different state — non-determinism in your code or data).",
                            frame,
                            bm.Checksum,
                            actual
                        );
                    }
                    return;
                }
                if (bm.Frame < frame)
                {
                    return;
                }
            }
        }

        void EnforceCapacityLimits()
        {
            var maxCount = _settings.MaxBookmarkCount;
            var maxBytes = _settings.MaxBookmarkMemoryBytes;
            var unbounded = maxCount <= 0 && maxBytes <= 0;
            if (unbounded)
            {
                _isPausedByCapacity = false;
                return;
            }

            var overCount = maxCount > 0 && _bookmarks.Count > maxCount;
            var overBytes = maxBytes > 0 && _totalBytes > maxBytes;
            if (!overCount && !overBytes)
            {
                _isPausedByCapacity = false;
                return;
            }

            switch (_settings.OverflowAction)
            {
                case CapacityOverflowAction.DropOldest:
                    while (_bookmarks.Count > 0)
                    {
                        overCount = maxCount > 0 && _bookmarks.Count > maxCount;
                        overBytes = maxBytes > 0 && _totalBytes > maxBytes;
                        if (!overCount && !overBytes)
                        {
                            break;
                        }
                        var oldest = _bookmarks[0];
                        _totalBytes -= oldest.Data.Length;
                        _bookmarks.RemoveAt(0);
                    }
                    if (_bookmarks.Count > 0)
                    {
                        // Reflect the new earliest available bookmark so the
                        // timeline slider's range stays accurate.
                        _startFrame = _bookmarks[0].Frame;
                        // Trim queued inputs in lockstep with the dropped
                        // bookmarks. MaxClearFrame already returns
                        // bookmarks[0].Frame - 1 so the queue's next OnReadyForInputs
                        // would prune anyway — but doing it eagerly here keeps
                        // memory bounded within the same frame the cap was hit,
                        // and stops the queue growing unbounded under sustained
                        // overflow pressure.
                        if (!_world.IsDisposed)
                        {
                            _accessor
                                .GetEntityInputQueue()
                                .ClearInputsBeforeOrAt(_bookmarks[0].Frame - 1);
                        }
                    }
                    _isPausedByCapacity = false;
                    break;

                case CapacityOverflowAction.Pause:
                    if (!_isPausedByCapacity)
                    {
                        _log.Warning(
                            "Recorder hit capacity ({} bookmarks, {} bytes) — pausing fixed phase. "
                                + "Save, fork, or reset before resuming.",
                            _bookmarks.Count,
                            _totalBytes
                        );
                    }
                    _isPausedByCapacity = true;
                    if (!_world.IsDisposed)
                    {
                        _accessor.GetSystemRunner().FixedIsPaused = true;
                    }
                    break;
            }
        }
    }
}
