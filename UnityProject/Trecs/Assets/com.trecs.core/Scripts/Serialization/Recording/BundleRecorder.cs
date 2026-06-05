using System;
using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Runtime recorder that captures a Trecs <see cref="World"/> session into
    /// a self-contained <see cref="RecordingBundle"/>: the initial world
    /// snapshot, the EntityInputQueue, sparse desync-detection checksums, and
    /// optional auto-keyframes and user snapshots.
    ///
    /// Lifecycle: Start → (per-frame capture happens automatically
    /// while the simulation runs) → Stop → returned bundle is handed to
    /// <see cref="RecordingBundleSerializer.Save(RecordingBundle, System.IO.Stream)"/>
    /// for persistence. The recorder is reusable: Stop produces a bundle and
    /// resets internal state; Start can be called again on a fresh world.
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class BundleRecorder : IInputHistoryLocker, IDisposable
    {
        readonly TrecsLog _log;

        // Lifecycle / IO primitives (accessor + checksum calculator + reusable
        // buffers + queue serialization + locker registration) live on this
        // shared collaborator. BundleReplayer and the editor recorder
        // (TrecsRewindBuffer) also own one — see RecordingEngine docs for the
        // split rationale.
        readonly RecordingEngine _core;
        readonly BundleRecorderSettings _settings;

        readonly IterableDictionary<int, ulong> _checksums = new();
        readonly List<WorldSnapshot> _keyframes = new();
        readonly List<WorldSnapshot> _bookmarks = new();

        // Contiguous-byte[] capture buffer: the serializer fills _captureScratch (its two
        // sections), then we copy the contiguous form into a freshly allocated byte[] that
        // becomes a bundle payload. The allocation is deliberate — the buffer is ceded to the
        // bundle (which can outlive this recorder), so there is nothing to recycle.
        readonly SerializationData _captureScratch = new();

        IDisposable _frameSubscription;

        bool _isRecording;
        bool _disposed;
        bool _lockerRegistered;
        int _startFrame;
        int _lastKeyframeFrame;
        ReadOnlyMemory<byte> _initialSnapshot;

        public BundleRecorder(
            World world,
            SerializerRegistry serializerRegistry,
            BundleRecorderSettings settings,
            SnapshotSerializer snapshotSerializer
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            _log = world.Log;
            _settings = settings;
            // No OpaqueBlobPersistence: this runtime recorder never persists
            // opaque (eager) blob bytes — SaveSnapshot asserts its snapshots
            // reference none (see CapturePayload).
            _core = new RecordingEngine(
                world,
                serializerRegistry,
                snapshotSerializer,
                accessorLabel: nameof(BundleRecorder)
            );
        }

        public bool IsRecording => _isRecording;
        public int StartFrame => _startFrame;
        public IReadOnlyList<WorldSnapshot> Keyframes => _keyframes;
        public IReadOnlyList<WorldSnapshot> Bookmarks => _bookmarks;

        /// <summary>
        /// Number of per-frame checksums captured so far this recording.
        /// The full dict is only exposed via the <see cref="RecordingBundle"/>
        /// returned by <see cref="Stop"/>.
        /// </summary>
        public int ChecksumCount => _checksums.Count;

        /// <summary>
        /// IInputHistoryLocker implementation. Inputs at frames before the
        /// recording's start aren't part of the captured timeline, so the
        /// queue's normal cleanup is allowed to prune them.
        /// </summary>
        public int? MaxClearFrame
        {
            get
            {
                if (!_isRecording)
                {
                    return null;
                }
                return _startFrame - 1;
            }
        }

        /// <summary>
        /// Capture an initial snapshot of the world state and start recording
        /// inputs and checksums from the current frame.
        /// </summary>
        /// <exception cref="InvalidOperationException">A recording is already in progress.</exception>
        /// <exception cref="ObjectDisposedException">The recorder has been disposed.</exception>
        public void Start()
        {
            ThrowIfDisposed();
            if (_isRecording)
            {
                throw new InvalidOperationException(
                    "Cannot Start: a recording is already in progress. Call Stop first."
                );
            }

            _checksums.Clear();
            _keyframes.Clear();
            _bookmarks.Clear();

            _startFrame = _core.Accessor.FixedFrame;
            _lastKeyframeFrame = _startFrame;

            // Capture the initial snapshot bytes + checksum so the produced
            // bundle is self-contained.
            _initialSnapshot = CapturePayload(_settings.Version, out var initialChecksum);

            // Start frame's checksum goes in the dict so playback can verify
            // the post-LoadInitialState world matches the recorded initial
            // state at frame 0 of the timeline.
            _checksums[_startFrame] = initialChecksum;

            // Lock input history so the queue's periodic cleanup doesn't
            // prune frames the recording covers; clear pre-start inputs so
            // any client-side rollback noise from before Start doesn't end
            // up in the recording.
            EnsureLockerRegistered(true);
            _core.Accessor.GetEntityInputQueue().ClearInputsBeforeOrAt(_startFrame - 1);

            _frameSubscription = _core.SubscribeFixedUpdateCompleted(OnFixedUpdateCompleted);
            _isRecording = true;

            _log.Debug("Recording started at fixed frame {0}", _startFrame);
        }

        /// <summary>
        /// Stop recording and return a fully-populated <see cref="RecordingBundle"/>.
        /// The bundle is detached from the recorder; subsequent operations on
        /// the returned object don't see further captures. Pass it to
        /// <see cref="RecordingBundleSerializer"/> to persist.
        /// </summary>
        /// <exception cref="InvalidOperationException">No recording is in progress.</exception>
        /// <exception cref="ObjectDisposedException">The recorder has been disposed.</exception>
        public RecordingBundle Stop()
        {
            ThrowIfDisposed();
            if (!_isRecording)
            {
                throw new InvalidOperationException(
                    "Cannot Stop: no recording is currently in progress."
                );
            }

            var endFrame = _core.Accessor.FixedFrame;

            // Drop the history lock before assembling (which serializes the
            // queue) so the queue is in its post-recording state (the queue's
            // own Serialize captures whatever is currently held).
            _frameSubscription?.Dispose();
            _frameSubscription = null;
            EnsureLockerRegistered(false);
            _isRecording = false;

            var bundle = _core.AssembleBundle(
                _settings.Version,
                _startFrame,
                endFrame,
                _core.CreateBundleBlobRootSet(),
                _initialSnapshot,
                _keyframes.ToArray(),
                _bookmarks.ToArray(),
                WorldSnapshotListUtil.CopyChecksums(_checksums)
            );

            _log.Debug(
                "Recording stopped: {0} frames, {1} keyframes, {2} bookmarks, {3} checksums, {4} bytes input queue",
                endFrame - _startFrame,
                _keyframes.Count,
                _bookmarks.Count,
                _checksums.Count,
                bundle.InputQueue.Length
            );

            // Reset internal state so the recorder can be Started again. The
            // bundle owns its payload memory now; clearing here just detaches
            // our local references to the same buffers.
            _initialSnapshot = default;
            _checksums.Clear();
            _keyframes.Clear();
            _bookmarks.Clear();

            return bundle;
        }

        /// <summary>
        /// Capture an unlabeled full-state keyframe at the current fixed frame,
        /// outside the normal <see cref="BundleRecorderSettings.KeyframeIntervalSeconds"/>
        /// cadence. Useful for runtime-driven "this is an interesting moment"
        /// checkpoints (e.g. just before launching a network request, at level
        /// boundaries) without waiting for the next auto-cadence tick. Replaces
        /// any existing keyframe at the same frame, and resets the auto-keyframe
        /// cadence timer so the next auto-capture fires a full interval from
        /// here rather than redundantly moments later. Use
        /// <see cref="CaptureBookmarkAtCurrentFrame"/> instead when the marker
        /// needs a user-visible label.
        /// </summary>
        /// <returns>True iff the keyframe was captured.</returns>
        public bool CaptureKeyframeAtCurrentFrame()
        {
            ThrowIfDisposed();
            if (!_isRecording)
            {
                _log.Warning("CaptureKeyframeAtCurrentFrame called while not recording");
                return false;
            }
            if (_core.World.IsDisposed)
            {
                return false;
            }

            var frame = _core.Accessor.FixedFrame;
            CaptureKeyframeAt(frame);
            // Reset the auto-keyframe cadence timer so we don't emit a redundant
            // auto-keyframe moments after this manual one.
            _lastKeyframeFrame = frame;
            return true;
        }

        /// <summary>
        /// Capture a labelled full-state bookmark at the current fixed frame.
        /// Replaces any existing bookmark at the same frame. Bookmarks
        /// survive Save/Load and are independent of auto-keyframes and
        /// checksums.
        /// </summary>
        /// <returns>True iff the bookmark was captured.</returns>
        public bool CaptureBookmarkAtCurrentFrame(string label)
        {
            ThrowIfDisposed();
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }
            if (!_isRecording)
            {
                _log.Warning("CaptureBookmarkAtCurrentFrame called while not recording");
                return false;
            }
            if (_core.World.IsDisposed)
            {
                return false;
            }

            var bookmark = CaptureSnapshotPayload(
                _core.Accessor.FixedFrame,
                SnapshotKind.Bookmark,
                label
            );
            WorldSnapshotListUtil.InsertOrReplaceByFrame(_bookmarks, bookmark);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_isRecording)
            {
                _log.Warning(
                    "Disposing BundleRecorder while recording is active — recording data will be discarded"
                );
                _frameSubscription?.Dispose();
                _frameSubscription = null;
                EnsureLockerRegistered(false);
                _isRecording = false;
            }
        }

        void OnFixedUpdateCompleted()
        {
            if (!_isRecording)
            {
                return;
            }
            var frame = _core.Accessor.FixedFrame;

            // Keyframe cadence — full state snapshot captured at long intervals.
            var fixedDeltaTime = _core.Accessor.GetSystemRunner().FixedDeltaTime;
            var keyframeElapsed = (frame - _lastKeyframeFrame) * fixedDeltaTime;
            if (keyframeElapsed >= _settings.KeyframeIntervalSeconds)
            {
                CaptureKeyframeAt(frame);
                _lastKeyframeFrame = frame;
            }
        }

        void CaptureKeyframeAt(int frame)
        {
            var keyframe = CaptureSnapshotPayload(frame, SnapshotKind.Keyframe, label: "");
            // Auto-cadence captures always run at strictly increasing frames so
            // they would naturally append. CaptureKeyframeAtCurrentFrame can land
            // on the same frame as the most recent auto-keyframe though (manual
            // call right after the cadence tick), so replace-if-present rather
            // than emit a duplicate entry at the same frame.
            if (_keyframes.Count > 0 && _keyframes[_keyframes.Count - 1].FixedFrame == frame)
            {
                _keyframes[_keyframes.Count - 1] = keyframe;
                return;
            }
            _keyframes.Add(keyframe);
        }

        // Capture the current world state into a freshly allocated contiguous byte[] payload,
        // plus its checksum. The serializer fills the reused _captureScratch SerializationData;
        // we copy the contiguous form into a buffer sized exactly to it. The buffer is ceded to
        // the bundle (it can outlive this recorder), so the allocation is deliberate — there is
        // nothing to recycle.
        ReadOnlyMemory<byte> CapturePayload(int version, out ulong checksum)
        {
            // No opaqueBlobIdsOut: this runtime recorder doesn't persist opaque (eager) blob
            // bytes, so SaveSnapshot asserts the snapshot references none.
            _core.SaveSnapshot(version, _captureScratch);
            var buffer = new byte[_captureScratch.ContiguousSize];
            _captureScratch.CopyContiguousTo(buffer);
            checksum = _captureScratch.ComputeContiguousChecksum();
            return buffer;
        }

        WorldSnapshot CaptureSnapshotPayload(int frame, SnapshotKind kind, string label)
        {
            var payload = CapturePayload(_settings.Version, out var checksum);
            // Single source of truth for every captured frame's checksum.
            // Idempotent with the cadence-driven write in OnFixedUpdateCompleted
            // when the two align on the same frame.
            _checksums[frame] = checksum;
            return new WorldSnapshot
            {
                FixedFrame = frame,
                Kind = kind,
                Label = label,
                Payload = payload,
            };
        }

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

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BundleRecorder));
            }
        }
    }
}
