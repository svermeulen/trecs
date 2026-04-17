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

        readonly WorldStateSerializer _worldStateSerializer;
        readonly RecordingChecksumCalculator _checksumCalculator;
        readonly BookmarkSerializer _bookmarkSerializer;
        readonly SerializationBuffer _buffer;
        readonly SerializationBuffer _checksumBuffer;
        readonly WorldAccessor _world;

        PlaybackState _state = PlaybackState.Idle;
        RecordingMetadata _playbackMetadata;
        int _version;
        bool _disposed;

        public PlaybackHandler(
            WorldStateSerializer worldStateSerializer,
            BookmarkSerializer bookmarkSerializer,
            SerializerRegistry registry,
            World world
        )
        {
            if (worldStateSerializer == null)
                throw new ArgumentNullException(nameof(worldStateSerializer));
            if (bookmarkSerializer == null)
                throw new ArgumentNullException(nameof(bookmarkSerializer));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _worldStateSerializer = worldStateSerializer;
            _checksumCalculator = new RecordingChecksumCalculator(worldStateSerializer);
            _bookmarkSerializer = bookmarkSerializer;
            _buffer = new SerializationBuffer(registry);
            _checksumBuffer = new SerializationBuffer(registry);

            _world = world.CreateAccessor();
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
        /// Load an initial-state bookmark and restore it into the live world,
        /// optionally verifying a post-deserialization checksum.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bookmarkStream"/> is null.</exception>
        /// <exception cref="SerializationException">The bookmark payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public bool LoadInitialState(
            Stream bookmarkStream,
            uint? expectedInitialChecksum,
            int version
        )
        {
            ThrowIfDisposed();
            if (bookmarkStream == null)
                throw new ArgumentNullException(nameof(bookmarkStream));

            _bookmarkSerializer.LoadBookmark(bookmarkStream);

            if (expectedInitialChecksum.HasValue)
            {
                VerifyPostDeserializationChecksum(expectedInitialChecksum.Value, version);
            }

            return true;
        }

        /// <summary>
        /// Load an initial-state bookmark from a file path.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="bookmarkPath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="bookmarkPath"/>.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public bool LoadInitialState(
            string bookmarkPath,
            uint? expectedInitialChecksum,
            int version
        )
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(bookmarkPath))
                throw new ArgumentException("bookmarkPath must be non-empty", nameof(bookmarkPath));
            if (!File.Exists(bookmarkPath))
                throw new FileNotFoundException("Bookmark file not found", bookmarkPath);

            using var fs = File.OpenRead(bookmarkPath);
            return LoadInitialState(fs, expectedInitialChecksum, version);
        }

        void VerifyPostDeserializationChecksum(uint expectedChecksum, int version)
        {
            var postDeserializeChecksum = _checksumCalculator.CalculateCurrentChecksum(
                version: version,
                _checksumBuffer
            );

            if (postDeserializeChecksum == expectedChecksum)
            {
                _log.Info(
                    "Post-deserialization checksum MATCHES ({}). Serialization OK - any desyncs occur during simulation.",
                    postDeserializeChecksum
                );
            }
            else
            {
                _log.Error(
                    "Post-deserialization checksum MISMATCH! Expected: {}, Got: {}",
                    expectedChecksum,
                    postDeserializeChecksum
                );
                _log.Error(
                    "This indicates a serialization/deserialization issue, NOT a simulation issue."
                );
            }
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

            RecordingMetadata recordingMetadata;
            try
            {
                recordingMetadata = _buffer.Read<RecordingMetadata>("metadata");

                var queueForRead = _world.GetEntityInputQueue();
                queueForRead.ClearAllInputs();
                queueForRead.Deserialize(
                    new Trecs.Serialization.TrecsSerializationReaderAdapter(_buffer)
                );

                var sentinelValue = _buffer.Read<int>("sentinel");
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

                recordingMetadata = new RecordingMetadata(
                    recordingMetadata.Version,
                    adjustedStartFrame,
                    adjustedEndFrame,
                    adjustedChecksums,
                    recordingMetadata.BlobIds
                );
            }

            _playbackMetadata = recordingMetadata;

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

            var result = new PlaybackTickResult();

            if (_state != PlaybackState.Desynced)
            {
                var checksums = _playbackMetadata.Checksums;
                var currentFrame = _world.FixedFrame;

                _log.Trace("Completed playback frame {}", currentFrame);

                if (checksums.TryGetValue(currentFrame, out var expectedChecksum))
                {
                    using var _ = TrecsProfiling.Start("CalculateCurrentChecksum");

                    var actualChecksum = _checksumCalculator.CalculateCurrentChecksum(
                        version: _version,
                        _checksumBuffer
                    );

                    result.ChecksumVerified = true;
                    result.ExpectedChecksum = expectedChecksum;
                    result.ActualChecksum = actualChecksum;

                    if (expectedChecksum == actualChecksum)
                    {
                        _log.Trace("Checksums match at frame {}", currentFrame);
                    }
                    else
                    {
                        _log.Warning(
                            "Desync detected at frame {}. checksum {} != {}",
                            currentFrame,
                            actualChecksum,
                            expectedChecksum
                        );
                        _state = PlaybackState.Desynced;
                        result.DesyncDetected = true;
                    }
                }
            }

            return result;
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
            for (int i = 0; i < _world.GetSystems().Count; i++)
            {
                var system = _world.GetSystems()[i];

                if (system.Metadata.RunPhase == SystemRunPhase.Input)
                {
                    Assert.That(_world.IsSystemEnabled(i) == !enable);
                    _world.SetSystemEnabled(i, enable);
                }
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
