using System;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public enum PlaybackState
    {
        Idle,
        Playing,
        Desynced,
    }

    /// <summary>
    /// Replays a recording captured by <see cref="RecordingHandler"/>,
    /// verifying checksums per frame and surfacing desyncs.
    ///
    /// Main-thread only.
    /// </summary>
    public class PlaybackHandler : IInputHistoryLocker, IDisposable
    {
        static readonly TrecsLog _log = new(nameof(PlaybackHandler));

        readonly IWorldStateSerializer _worldStateSerializer;
        readonly RecordingChecksumCalculator _checksumCalculator;
        readonly SnapshotSerializer _snapshotSerializer;
        readonly SerializationBuffer _buffer;
        readonly SerializationBuffer _checksumBuffer;
        readonly World _worldOwner;
        readonly WorldAccessor _world;

        PlaybackState _state = PlaybackState.Idle;
        RecordingMetadata _playbackMetadata;
        int _version;
        long _checksumFlags;
        bool _disposed;

        public PlaybackHandler(
            IWorldStateSerializer worldStateSerializer,
            SnapshotSerializer snapshotSerializer,
            SerializerRegistry registry,
            World world
        )
        {
            if (worldStateSerializer == null)
                throw new ArgumentNullException(nameof(worldStateSerializer));
            if (snapshotSerializer == null)
                throw new ArgumentNullException(nameof(snapshotSerializer));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _worldStateSerializer = worldStateSerializer;
            _checksumCalculator = new RecordingChecksumCalculator(worldStateSerializer);
            _snapshotSerializer = snapshotSerializer;
            _buffer = new SerializationBuffer(registry);
            _checksumBuffer = new SerializationBuffer(registry);

            _worldOwner = world;
            _world = world.CreateAccessor(AccessorRole.Unrestricted);
        }

        public PlaybackState State
        {
            get { return _state; }
        }

        public bool IsPlaying
        {
            get { return _state == PlaybackState.Playing || _state == PlaybackState.Desynced; }
        }

        public bool HasDesynced
        {
            get { return _state == PlaybackState.Desynced; }
        }

        public RecordingMetadata PlaybackMetadata
        {
            get { return _playbackMetadata; }
        }

        public int? MaxClearFrame
        {
            get
            {
                Assert.That(IsPlaying);
                return -1;
            }
        }

        /// <summary>
        /// Load an initial-state snapshot and restore it into the live world,
        /// optionally verifying a post-deserialization checksum against the
        /// value stored in the active recording's metadata.
        /// <see cref="StartPlayback"/> must have been called first so the
        /// checksum flags and schema version are known.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshotStream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Playback is not active.</exception>
        /// <exception cref="SerializationException">The snapshot payload is invalid, or
        /// <paramref name="expectedInitialChecksum"/> was supplied and the post-load
        /// checksum did not match — indicating a serialization/deserialization defect.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public void LoadInitialState(Stream snapshotStream, uint? expectedInitialChecksum)
        {
            ThrowIfDisposed();
            if (snapshotStream == null)
                throw new ArgumentNullException(nameof(snapshotStream));
            if (!IsPlaying)
                throw new InvalidOperationException(
                    "Cannot LoadInitialState: playback is not active. Call StartPlayback first."
                );

            _snapshotSerializer.LoadSnapshot(snapshotStream);

            if (expectedInitialChecksum.HasValue)
            {
                VerifyPostDeserializationChecksum(expectedInitialChecksum.Value);
            }
        }

        /// <summary>
        /// Load an initial-state snapshot from a file path.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="snapshotPath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="snapshotPath"/>.</exception>
        /// <exception cref="InvalidOperationException">Playback is not active.</exception>
        /// <exception cref="SerializationException">The snapshot payload is invalid, or the
        /// post-load checksum did not match <paramref name="expectedInitialChecksum"/>.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public void LoadInitialState(string snapshotPath, uint? expectedInitialChecksum)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(snapshotPath))
                throw new ArgumentException("snapshotPath must be non-empty", nameof(snapshotPath));
            if (!File.Exists(snapshotPath))
                throw new FileNotFoundException("Snapshot file not found", snapshotPath);

            using var fs = File.OpenRead(snapshotPath);
            LoadInitialState(fs, expectedInitialChecksum);
        }

        void VerifyPostDeserializationChecksum(uint expectedChecksum)
        {
            var postDeserializeChecksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _version,
                _checksumBuffer,
                _checksumFlags
            );

            if (postDeserializeChecksum != expectedChecksum)
            {
                throw new SerializationException(
                    $"Post-deserialization checksum MISMATCH (expected: {expectedChecksum}, got: {postDeserializeChecksum}). "
                        + "This indicates a serialization/deserialization defect — not a simulation desync."
                );
            }

            _log.Info(
                "Post-deserialization checksum MATCHES ({}). Serialization OK - any desyncs occur during simulation.",
                postDeserializeChecksum
            );
        }

        /// <summary>
        /// Start playback from <paramref name="recordingStream"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="recordingStream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Playback is already active.</exception>
        /// <exception cref="SerializationException">The recording payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public void StartPlayback(Stream recordingStream, PlaybackStartParams startParams)
        {
            ThrowIfDisposed();
            if (recordingStream == null)
                throw new ArgumentNullException(nameof(recordingStream));
            if (_state != PlaybackState.Idle)
                throw new InvalidOperationException(
                    "Cannot StartPlayback: playback is already active. Call EndPlayback first."
                );

            _version = startParams.Version;

            RecordingMetadata recordingMetadata;
            try
            {
                _buffer.ClearMemoryStream();
                recordingStream.CopyTo(_buffer.MemoryStream);
                if (_buffer.MemoryStream.Length == 0)
                {
                    throw new SerializationException(
                        "Recording stream is empty — cannot start playback on an empty recording."
                    );
                }
                _buffer.MemoryStream.Position = 0;
                _buffer.StartRead();
                recordingMetadata = _buffer.Read<RecordingMetadata>("metadata");

                var queueForRead = _world.GetEntityInputQueue();
                queueForRead.ClearAllInputs();
                queueForRead.Deserialize(new TrecsSerializationReaderAdapter(_buffer));

                var sentinelValue = _buffer.Read<int>("recordingSentinel");
                Assert.IsEqual(sentinelValue, TrecsConstants.RecordingSentinelValue);

                _buffer.StopRead(verifySentinel: true);
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }

            var entityInputQueue = _world.GetEntityInputQueue();
            entityInputQueue.AddHistoryLocker(this);

            // InputsOnly mode: remap input frames to be relative to current frame
            if (startParams.InputsOnly)
            {
                var currentFrame = _world.FixedFrame;
                var recordingStartFrame = recordingMetadata.StartFixedFrame;
                var frameOffset = currentFrame - recordingStartFrame;

                _log.Info(
                    "InputsOnly mode: remapping input frames with offset {} (current frame {} - recording start frame {})",
                    frameOffset,
                    currentFrame,
                    recordingStartFrame
                );

                entityInputQueue.RemapFrameOffsets(frameOffset);

                var adjustedStartFrame = recordingMetadata.StartFixedFrame + frameOffset;
                var adjustedEndFrame = recordingMetadata.EndFixedFrame + frameOffset;

                var adjustedChecksums = new DenseDictionary<int, uint>();
                foreach (var (frame, checksum) in recordingMetadata.Checksums)
                {
                    adjustedChecksums.Add(frame + frameOffset, checksum);
                }

                recordingMetadata = new RecordingMetadata
                {
                    Version = recordingMetadata.Version,
                    StartFixedFrame = adjustedStartFrame,
                    EndFixedFrame = adjustedEndFrame,
                    ChecksumFlags = recordingMetadata.ChecksumFlags,
                    Checksums = adjustedChecksums,
                    BlobIds = recordingMetadata.BlobIds,
                };
            }

            _playbackMetadata = recordingMetadata;
            _checksumFlags = recordingMetadata.ChecksumFlags;

            SetInputsEnabled(false);
            _state = PlaybackState.Playing;
        }

        /// <summary>
        /// Start playback from a recording at <paramref name="recordingPath"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="recordingPath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="recordingPath"/>.</exception>
        /// <exception cref="InvalidOperationException">Playback is already active.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public void StartPlayback(string recordingPath, PlaybackStartParams startParams)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(recordingPath))
                throw new ArgumentException(
                    "recordingPath must be non-empty",
                    nameof(recordingPath)
                );
            if (!File.Exists(recordingPath))
                throw new FileNotFoundException("Recording file not found", recordingPath);

            using var fs = File.OpenRead(recordingPath);
            StartPlayback(fs, startParams);
        }

        /// <summary>
        /// Call each fixed update during playback. Verifies the current-frame
        /// checksum (if one was recorded for this frame) and surfaces desyncs
        /// via the returned <see cref="PlaybackTickResult"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Playback is not active.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public PlaybackTickResult TickPlayback()
        {
            ThrowIfDisposed();
            if (!IsPlaying)
                throw new InvalidOperationException(
                    "Cannot TickPlayback: playback is not active. Call StartPlayback first."
                );

            if (_state == PlaybackState.Desynced)
            {
                return new PlaybackTickResult();
            }

            var checksums = _playbackMetadata.Checksums;
            var currentFrame = _world.FixedFrame;

            _log.Trace("Completed playback frame {}", currentFrame);

            if (!checksums.TryGetValue(currentFrame, out var expectedChecksum))
            {
                return new PlaybackTickResult();
            }

            using var _ = TrecsProfiling.Start("CalculateCurrentChecksum");

            var actualChecksum = _checksumCalculator.CalculateCurrentChecksum(
                version: _version,
                _checksumBuffer,
                _checksumFlags
            );

            if (expectedChecksum != actualChecksum)
            {
                _log.Warning(
                    "Desync detected at frame {}. checksum {} != {}",
                    currentFrame,
                    actualChecksum,
                    expectedChecksum
                );
                _state = PlaybackState.Desynced;
            }
            else
            {
                _log.Trace("Checksums match at frame {}", currentFrame);
            }

            return new PlaybackTickResult
            {
                ExpectedChecksum = expectedChecksum,
                ActualChecksum = actualChecksum,
            };
        }

        /// <summary>
        /// End the current playback session.
        /// </summary>
        /// <exception cref="InvalidOperationException">Playback is not active.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public void EndPlayback()
        {
            ThrowIfDisposed();
            if (!IsPlaying)
                throw new InvalidOperationException("Cannot EndPlayback: playback is not active.");

            var entityInputQueue = _world.GetEntityInputQueue();

            entityInputQueue.RemoveHistoryLocker(this);
            entityInputQueue.ClearFutureInputsAfterOrAt(_world.FixedFrame);

            SetInputsEnabled(true);

            _playbackMetadata = null;
            _state = PlaybackState.Idle;

            _log.Info("Playback ended");
        }

        void SetInputsEnabled(bool enable)
        {
            for (int i = 0; i < _worldOwner.SystemCount; i++)
            {
                if (_worldOwner.GetSystemMetadata(i).Phase != SystemPhase.Input)
                {
                    continue;
                }
                Assert.That(_world.IsSystemEnabled(i, EnableChannel.Playback) == !enable);
                _world.SetSystemEnabled(i, EnableChannel.Playback, enable);
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlaybackHandler));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (IsPlaying)
            {
                _log.Warning(
                    "Disposing PlaybackHandler while playback is active — ending playback first"
                );
                // EndPlayback checks _disposed first so call its implementation inline.
                var entityInputQueue = _world.GetEntityInputQueue();
                entityInputQueue.RemoveHistoryLocker(this);
                entityInputQueue.ClearFutureInputsAfterOrAt(_world.FixedFrame);
                SetInputsEnabled(true);
                _playbackMetadata = null;
                _state = PlaybackState.Idle;
            }

            _buffer.Dispose();
            _checksumBuffer.Dispose();
        }
    }
}
