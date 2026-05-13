using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Runtime recorder that captures a Trecs <see cref="World"/> session into
    /// a self-contained <see cref="RecordingBundle"/>: the initial world
    /// snapshot, the EntityInputQueue, sparse desync-detection checksums, and
    /// optional auto-anchors and user snapshots.
    ///
    /// Lifecycle: Initialize → Start → (per-frame capture happens automatically
    /// while the simulation runs) → Stop → returned bundle is handed to
    /// <see cref="RecordingBundleSerializer.Save(RecordingBundle, System.IO.Stream)"/>
    /// for persistence. The recorder is reusable: Stop produces a bundle and
    /// resets internal state; Start can be called again on a fresh world.
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class BundleRecorder : IInputHistoryLocker, IDisposable
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly World _world;
        readonly IWorldStateSerializer _stateSerializer;
        readonly SerializerRegistry _serializerRegistry;
        readonly BundleRecorderSettings _settings;
        readonly SnapshotSerializer _snapshotSerializer;

        readonly DenseDictionary<int, uint> _checksums = new();
        readonly List<BundleAnchor> _anchors = new();
        readonly List<BundleSnapshot> _snapshots = new();

        WorldAccessor _accessor;
        RecordingChecksumCalculator _checksumCalculator;
        SerializationBuffer _checksumBuffer;
        IDisposable _frameSubscription;

        bool _isRecording;
        bool _disposed;
        bool _lockerRegistered;
        int _startFrame;
        int _lastAnchorFrame;
        byte[] _initialSnapshot;
        uint _initialChecksum;

        public BundleRecorder(
            World world,
            IWorldStateSerializer stateSerializer,
            SerializerRegistry serializerRegistry,
            BundleRecorderSettings settings,
            SnapshotSerializer snapshotSerializer
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (stateSerializer == null)
                throw new ArgumentNullException(nameof(stateSerializer));
            if (serializerRegistry == null)
                throw new ArgumentNullException(nameof(serializerRegistry));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (snapshotSerializer == null)
                throw new ArgumentNullException(nameof(snapshotSerializer));
            if (settings.ChecksumFrameInterval < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(settings) + "." + nameof(BundleRecorderSettings.ChecksumFrameInterval),
                    settings.ChecksumFrameInterval,
                    "ChecksumFrameInterval must be >= 1"
                );

            _world = world;
            _stateSerializer = stateSerializer;
            _serializerRegistry = serializerRegistry;
            _settings = settings;
            _snapshotSerializer = snapshotSerializer;
        }

        public bool IsRecording => _isRecording;
        public int StartFrame => _startFrame;
        public IReadOnlyList<BundleAnchor> Anchors => _anchors;
        public IReadOnlyList<BundleSnapshot> Snapshots => _snapshots;

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

        public void Initialize()
        {
            ThrowIfDisposed();
            _accessor = _world.CreateAccessor(AccessorRole.Unrestricted, nameof(BundleRecorder));
            _checksumCalculator = new RecordingChecksumCalculator(_stateSerializer);
            _checksumBuffer = new SerializationBuffer(_serializerRegistry);
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
            _anchors.Clear();
            _snapshots.Clear();

            _startFrame = _accessor.FixedFrame;
            _lastAnchorFrame = _startFrame;

            // Capture the initial snapshot bytes + checksum so the produced
            // bundle is self-contained.
            using (var ms = new MemoryStream())
            {
                _snapshotSerializer.SaveSnapshot(_settings.Version, ms, includeTypeChecks: true);
                _initialSnapshot = ms.ToArray();
            }
            _initialChecksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _settings.Version,
                _checksumBuffer,
                _settings.ChecksumFlags
            );

            // Start frame's checksum also goes in the dict so playback can
            // verify the post-LoadInitialState world matches the recorded
            // initial state at frame 0 of the timeline.
            _checksums[_startFrame] = _initialChecksum;

            // Lock input history so the queue's periodic cleanup doesn't
            // prune frames the recording covers; clear pre-start inputs so
            // any client-side rollback noise from before Start doesn't end
            // up in the recording.
            var queue = _accessor.GetEntityInputQueue();
            queue.AddHistoryLocker(this);
            queue.ClearInputsBeforeOrAt(_startFrame - 1);
            _lockerRegistered = true;

            _frameSubscription = _accessor.Events.OnFixedUpdateCompleted(OnFixedUpdateCompleted);
            _isRecording = true;

            _log.Debug("Recording started at fixed frame {}", _startFrame);
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

            var endFrame = _accessor.FixedFrame;
            var fixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;

            // Drop the history lock before serializing the queue so the queue
            // is in its post-recording state (the queue's own Serialize
            // captures whatever is currently held).
            _frameSubscription?.Dispose();
            _frameSubscription = null;
            EnsureLockerRegistered(false);
            _isRecording = false;

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

            var blobs = new DenseHashSet<BlobId>();
            _world.GetBlobCache().GetAllActiveBlobIds(blobs);

            var bundle = new RecordingBundle
            {
                Header = new BundleHeader
                {
                    Version = _settings.Version,
                    StartFixedFrame = _startFrame,
                    EndFixedFrame = endFrame,
                    FixedDeltaTime = fixedDeltaTime,
                    ChecksumFlags = _settings.ChecksumFlags,
                    BlobIds = blobs,
                },
                InitialSnapshot = _initialSnapshot,
                InitialSnapshotChecksum = _initialChecksum,
                InputQueue = queueBytes,
                Checksums = CopyChecksums(),
                Anchors = _anchors.ToArray(),
                Snapshots = _snapshots.ToArray(),
            };

            _log.Debug(
                "Recording stopped: {} frames, {} anchors, {} snapshots, {} checksums, {} bytes input queue",
                endFrame - _startFrame,
                _anchors.Count,
                _snapshots.Count,
                _checksums.Count,
                queueBytes.Length
            );

            // Reset internal state so the recorder can be Started again.
            _initialSnapshot = null;
            _initialChecksum = 0;
            _checksums.Clear();
            _anchors.Clear();
            _snapshots.Clear();

            return bundle;
        }

        /// <summary>
        /// Capture an unlabeled full-state anchor at the current fixed frame,
        /// outside the normal <see cref="BundleRecorderSettings.AnchorIntervalSeconds"/>
        /// cadence. Useful for runtime-driven "this is an interesting moment"
        /// checkpoints (e.g. just before launching a network request, at level
        /// boundaries) without waiting for the next auto-cadence tick. Replaces
        /// any existing anchor at the same frame, and resets the auto-anchor
        /// cadence timer so the next auto-capture fires a full interval from
        /// here rather than redundantly moments later. Use
        /// <see cref="CaptureSnapshotAtCurrentFrame"/> instead when the marker
        /// needs a user-visible label.
        /// </summary>
        /// <returns>True iff the anchor was captured.</returns>
        public bool CaptureAnchorAtCurrentFrame()
        {
            ThrowIfDisposed();
            if (!_isRecording)
            {
                _log.Warning("CaptureAnchorAtCurrentFrame called while not recording");
                return false;
            }
            if (_world.IsDisposed)
            {
                return false;
            }

            var frame = _accessor.FixedFrame;
            CaptureAnchorAt(frame);
            // Reset the auto-anchor cadence timer so we don't emit a redundant
            // auto-anchor moments after this manual one.
            _lastAnchorFrame = frame;
            return true;
        }

        /// <summary>
        /// Capture a labeled full-state snapshot at the current fixed frame.
        /// Replaces any existing snapshot at the same frame. Snapshots
        /// survive Save/Load and are independent of auto-anchors and
        /// checksums.
        /// </summary>
        /// <returns>True iff the snapshot was captured.</returns>
        public bool CaptureSnapshotAtCurrentFrame(string label)
        {
            ThrowIfDisposed();
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
                _settings.ChecksumFlags
            );
            var snapshot = new BundleSnapshot
            {
                FixedFrame = _accessor.FixedFrame,
                Checksum = checksum,
                Label = label,
                Payload = bytes,
            };

            for (int i = 0; i < _snapshots.Count; i++)
            {
                if (_snapshots[i].FixedFrame == snapshot.FixedFrame)
                {
                    _snapshots[i] = snapshot;
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

            _checksumBuffer?.Dispose();
            _checksumBuffer = null;
        }

        void OnFixedUpdateCompleted()
        {
            if (!_isRecording)
            {
                return;
            }
            var frame = _accessor.FixedFrame;

            // Per-frame checksum cadence — sparse compared to fixed frames,
            // dense compared to anchors. Catches desyncs close to where they
            // happen. Skipped in profiling builds (the per-frame hash is
            // measurable; anchors and inputs are still captured).
#if !TRECS_IS_PROFILING
            if ((frame - _startFrame) % _settings.ChecksumFrameInterval == 0)
            {
                var checksum = _checksumCalculator.CalculateCurrentChecksum(
                    version: _settings.Version,
                    _checksumBuffer,
                    _settings.ChecksumFlags
                );
                // Overwrite rather than Add: client-style rollbacks may
                // re-record the same frame with new state.
                _checksums[frame] = checksum;
            }
#endif

            // Anchor cadence — full state snapshot captured at long intervals.
            var fixedDeltaTime = _accessor.GetSystemRunner().FixedDeltaTime;
            var anchorElapsed = (frame - _lastAnchorFrame) * fixedDeltaTime;
            if (anchorElapsed >= _settings.AnchorIntervalSeconds)
            {
                CaptureAnchorAt(frame);
                _lastAnchorFrame = frame;
            }
        }

        void CaptureAnchorAt(int frame)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                _snapshotSerializer.SaveSnapshot(_settings.Version, ms, includeTypeChecks: true);
                bytes = ms.ToArray();
            }
            var checksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _settings.Version,
                _checksumBuffer,
                _settings.ChecksumFlags
            );
            var anchor = new BundleAnchor
            {
                FixedFrame = frame,
                Checksum = checksum,
                Payload = bytes,
            };

            // Auto-cadence captures always run at strictly increasing frames so
            // they would naturally append. CaptureAnchorAtCurrentFrame can land
            // on the same frame as the most recent auto-anchor though (manual
            // call right after the cadence tick), so replace-if-present rather
            // than emit a duplicate entry at the same frame.
            if (_anchors.Count > 0 && _anchors[_anchors.Count - 1].FixedFrame == frame)
            {
                _anchors[_anchors.Count - 1] = anchor;
                return;
            }
            _anchors.Add(anchor);
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

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BundleRecorder));
            }
        }
    }
}
