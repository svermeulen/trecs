using System;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Records inputs + periodic world-state checksums across a span of fixed
    /// frames, then writes the recording to a stream or file. The resulting
    /// recording can be replayed by <see cref="PlaybackHandler"/>.
    ///
    /// Main-thread only.
    /// </summary>
    public class RecordingHandler : IInputHistoryLocker, IDisposable
    {
        static readonly TrecsLog _log = new(nameof(RecordingHandler));

        readonly WorldStateSerializer _worldStateSerializer;
        readonly SerializationBuffer _buffer;
        readonly IDisposable _eventSubscription;
        readonly SimpleSubject _checksumRecorded = new();
        readonly RecordingChecksumCalculator _checksumCalculator;
        readonly BlobCache _blobCache;

        WorldAccessor _world;
        bool _isRecording;
        bool _disposed;
        RecordingInfo _recordingInfo;
        int _recordingVersion;

        public RecordingHandler(
            WorldStateSerializer worldStateSerializer,
            SerializerRegistry registry,
            World world
        )
        {
            if (worldStateSerializer == null)
                throw new ArgumentNullException(nameof(worldStateSerializer));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _worldStateSerializer = worldStateSerializer;
            _checksumCalculator = new RecordingChecksumCalculator(worldStateSerializer);
            _blobCache = world.GetBlobCache();
            _buffer = new SerializationBuffer(registry);

            _world = world.CreateAccessor();
            _eventSubscription = _world.Events.OnFixedUpdateCompleted(OnFixedUpdateCompleted);
        }

        public bool IsRecording
        {
            get { return _isRecording; }
        }

        public ISimpleObservable ChecksumRecorded
        {
            get { return _checksumRecorded; }
        }

        public int? MaxClearFrame
        {
            get
            {
                Assert.That(_recordingInfo != null);

                // Allow clearing history for all frames before recording started
                // but not after, since we need to serialize that data when
                // recording completes
                return _recordingInfo.StartFrame - 1;
            }
        }

        /// <summary>
        /// Start capturing inputs + checksums from the current fixed frame.
        /// </summary>
        /// <param name="version">User-defined schema version stored in the recording header.</param>
        /// <param name="checksumsEnabled">When false, checksums are skipped (useful when profiling — checksums are the main recording cost).</param>
        /// <param name="checksumFrameInterval">One checksum every N fixed frames. Must be >= 1.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="checksumFrameInterval"/> is less than 1.</exception>
        /// <exception cref="InvalidOperationException">A recording is already in progress.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public void StartRecording(int version, bool checksumsEnabled, int checksumFrameInterval)
        {
#if TRECS_IS_PROFILING
            _log.Warning("Recording while profiling is enabled");
#endif
            ThrowIfDisposed();
            if (_isRecording)
                throw new InvalidOperationException(
                    "Cannot StartRecording: a recording is already in progress. Call EndRecording first."
                );
            if (checksumFrameInterval < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(checksumFrameInterval),
                    checksumFrameInterval,
                    "checksumFrameInterval must be >= 1"
                );

            _isRecording = true;
            _recordingVersion = version;

            _recordingInfo = new RecordingInfo
            {
                StartFrame = _world.FixedFrame,
                Checksums = new DenseDictionary<int, uint>(),
                BlobIds = new DenseHashSet<BlobId>(),
                ChecksumsEnabled = checksumsEnabled,
                ChecksumFrameInterval = checksumFrameInterval,
            };

            var entityInputQueue = _world.GetEntityInputQueue();

            entityInputQueue.AddHistoryLocker(this);

            // It's important that we clear past inputs here, because this will
            // prevent any client corrections to run before our recording started
            entityInputQueue.ClearInputsBeforeOrAt(_world.FixedFrame - 1);

            _blobCache.GetAllActiveBlobIds(_recordingInfo.BlobIds);
        }

        /// <summary>
        /// Finish the active recording, write the resulting bytes to
        /// <paramref name="stream"/>, and return the metadata describing the
        /// recording.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">No recording is in progress.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public RecordingMetadata EndRecording(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!_isRecording)
                throw new InvalidOperationException(
                    "Cannot EndRecording: no recording is currently in progress."
                );

            var finalFrame = _world.FixedFrame;
            _blobCache.GetAllActiveBlobIds(_recordingInfo.BlobIds);

            var metadata = new RecordingMetadata(
                version: _recordingVersion,
                startFixedFrame: _recordingInfo.StartFrame,
                endFixedFrame: finalFrame,
                checksums: _recordingInfo.Checksums,
                blobIds: _recordingInfo.BlobIds
            );

            long recordingNumBytes;
            try
            {
                _buffer.ClearMemoryStream();
                _buffer.StartWrite(version: _recordingVersion, includeTypeChecks: true);
                long bytesStart = _buffer.NumBytesWritten;
                _buffer.Write("metadata", metadata);
                _log.Debug(
                    "Serialized metadata ({0.00} kb)",
                    (_buffer.NumBytesWritten - bytesStart) / 1024f
                );

                var entityInputQueue = _world.GetEntityInputQueue();
                entityInputQueue.Serialize(new TrecsSerializationWriterAdapter(_buffer));
                _log.Debug(
                    "Serialized EntityInputQueue ({0.00} kb)",
                    (_buffer.NumBytesWritten - bytesStart) / 1024f
                );

                _buffer.Write<int>("recordingSentinel", TrecsConstants.RecordingSentinelValue);
                recordingNumBytes = _buffer.EndWrite();
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                // Preserve in-progress recording state so the caller can retry or End again;
                // but un-wire the history locker so we don't lock input history indefinitely.
                _world.GetEntityInputQueue().RemoveHistoryLocker(this);
                _recordingInfo = null;
                _isRecording = false;
                throw;
            }

            _buffer.MemoryStream.Position = 0;
            _buffer.MemoryStream.CopyTo(stream);

            var startFrame = _recordingInfo.StartFrame;
            var numFrames = finalFrame - startFrame;
            _log.Debug(
                "Recording complete ({0.00} kb recording). Recorded {} frames and {} checksums",
                recordingNumBytes / 1024f,
                numFrames,
                _recordingInfo.Checksums.Count
            );

            _world.GetEntityInputQueue().RemoveHistoryLocker(this);
            _recordingInfo = null;
            _isRecording = false;
            return metadata;
        }

        /// <summary>
        /// Finish the active recording and write the resulting bytes to
        /// <paramref name="filePath"/>. Creates the parent directory if needed.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="InvalidOperationException">No recording is in progress.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public RecordingMetadata EndRecording(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = File.Create(filePath);
            return EndRecording(fs);
        }

        /// <summary>
        /// Read just the recording metadata from <paramref name="stream"/> without
        /// starting playback. Useful for displaying recording-list info (duration,
        /// referenced blobs, schema version) without loading the full recording.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">A recording is currently in progress.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public RecordingMetadata PeekMetadata(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (_isRecording)
                throw new InvalidOperationException(
                    "Cannot PeekMetadata while a recording is in progress."
                );

            try
            {
                _buffer.ClearMemoryStream();
                stream.CopyTo(_buffer.MemoryStream);
                if (_buffer.MemoryStream.Length == 0)
                {
                    throw new SerializationException(
                        "Recording stream is empty — cannot peek metadata."
                    );
                }
                _buffer.MemoryStream.Position = 0;
                _buffer.StartRead();
                var metadata = _buffer.Read<RecordingMetadata>("metadata");
                _buffer.StopRead(verifySentinel: false);
                return metadata;
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read just the recording metadata from <paramref name="filePath"/> without
        /// starting playback.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        /// <exception cref="InvalidOperationException">A recording is currently in progress.</exception>
        /// <exception cref="SerializationException">The file is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The handler has been disposed.</exception>
        public RecordingMetadata PeekMetadata(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Recording file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return PeekMetadata(fs);
        }

        void OnFixedUpdateCompleted()
        {
            // serialization is well optimized but still causes noticable spikes when recording
            // so we disable checksums when profiling
            // this is helpful because we do often make use of backup recording while profiling
#if !TRECS_IS_PROFILING
            if (_isRecording)
            {
                Assert.IsNotNull(_recordingInfo);

                var currentFrame = _world.FixedFrame;

                if (
                    _recordingInfo.ChecksumsEnabled
                    && (currentFrame % _recordingInfo.ChecksumFrameInterval == 0)
                )
                {
                    var checksum = _checksumCalculator.CalculateCurrentChecksum(
                        version: _recordingVersion,
                        _buffer
                    );

                    // Note that we can't do Add here because we sometimes record the client, which can do rollbacks,
                    // so we need to overwrite the checksum in this case
                    _recordingInfo.Checksums[currentFrame] = checksum;

                    _checksumRecorded.Invoke();
                }
            }
#endif
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RecordingHandler));
            }
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
                    "Disposing RecordingHandler while recording is active — recording data will be discarded"
                );
                _recordingInfo = null;
                _isRecording = false;
                _world.GetEntityInputQueue().RemoveHistoryLocker(this);
            }

            _eventSubscription.Dispose();
            _buffer.Dispose();
        }

        class RecordingInfo
        {
            public int StartFrame;
            public bool ChecksumsEnabled;
            public int ChecksumFrameInterval;
            public DenseDictionary<int, uint> Checksums;
            public DenseHashSet<BlobId> BlobIds;
        }
    }
}
