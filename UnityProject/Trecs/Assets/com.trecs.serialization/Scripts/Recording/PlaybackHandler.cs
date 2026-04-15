using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class PlaybackHandler : IInputHistoryLocker, IDisposable
    {
        static readonly TrecsLog _log = new(nameof(PlaybackHandler));

        readonly IGameStateSerializer _gameStateSerializer;
        readonly RecordingChecksumCalculator _checksumCalculator;
        readonly BookmarkSerializer _bookmarkSerializer;
        readonly SerializationBuffer _checksumSerializerHelper;
        readonly WorldAccessor _world;
        bool _isPlaying;
        bool _hasDesynced;
        DebugRecordingMetadata _playbackMetadata;
        int _version;

        public PlaybackHandler(
            IGameStateSerializer gameStateSerializer,
            RecordingChecksumCalculator checksumCalculator,
            BookmarkSerializer bookmarkSerializer,
            SerializerRegistry serializerManager,
            World ecsProvider
        )
        {
            _gameStateSerializer = gameStateSerializer;
            _checksumCalculator = checksumCalculator;
            _bookmarkSerializer = bookmarkSerializer;
            _checksumSerializerHelper = new SerializationBuffer(serializerManager);

            _world = ecsProvider.CreateAccessor();
        }

        public bool IsPlaying
        {
            get { return _isPlaying; }
        }

        public bool HasDesynced
        {
            get { return _hasDesynced; }
        }

        public DebugRecordingMetadata PlaybackMetadata
        {
            get { return _playbackMetadata; }
        }

        public int? MaxClearFrame
        {
            get
            {
                Assert.That(_isPlaying);
                return -1;
            }
        }

        /// <summary>
        /// Load and deserialize initial state from a pre-loaded serializer helper.
        /// The caller is responsible for loading the initial state bookmark data into the
        /// serializerHelper's memory stream before calling this.
        /// Returns false if the static seed is incompatible.
        /// </summary>
        public bool LoadInitialState(
            SerializationBuffer serializerHelper,
            uint? expectedInitialChecksum,
            int version
        )
        {
            if (!_bookmarkSerializer.Load(serializerHelper))
            {
                return false;
            }

            if (expectedInitialChecksum.HasValue)
            {
                VerifyPostDeserializationChecksum(expectedInitialChecksum.Value, version);
            }

            return true;
        }

        void VerifyPostDeserializationChecksum(uint expectedChecksum, int version)
        {
            var postDeserializeChecksum = _checksumCalculator.CalculateCurrentChecksum(
                version: version,
                _checksumSerializerHelper,
                _gameStateSerializer.ChecksumSerializationFlags
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
        /// Start playback with recording data pre-loaded into params.SerializerHelper.
        /// The caller is responsible for loading the recording file data into the
        /// serializer helper's memory stream before calling this.
        /// </summary>
        public void StartPlayback(PlaybackStartParams startParams)
        {
            Assert.That(!_isPlaying);

            _version = startParams.Version;

            var serializerHelper = startParams.SerializerHelper;

            var succeeded = _gameStateSerializer.StartDeserialize(
                serializerHelper,
                startParams.SerializationFlags
            );
            Assert.That(succeeded);

            var recordingMetadata = serializerHelper.Read<DebugRecordingMetadata>("metadata");

            var entityInputQueue = _world.GetEntityInputQueue();

            entityInputQueue.ClearAllInputs();
            entityInputQueue.Deserialize(
                new Trecs.Serialization.TrecsSerializationReaderAdapter(serializerHelper)
            );

            entityInputQueue.AddHistoryLocker(this);

            var sentinelValue = serializerHelper.Read<int>("sentinel");
            Assert.IsEqual(sentinelValue, TrecsConstants.RecordingSentinelValue);

            serializerHelper.StopRead(verifySentinel: true);

            _hasDesynced = false;

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

                recordingMetadata = new DebugRecordingMetadata(
                    adjustedStartFrame,
                    adjustedEndFrame,
                    adjustedChecksums,
                    recordingMetadata.BlobIds
                );
            }

            _playbackMetadata = recordingMetadata;

            SetInputsEnabled(false);
            _isPlaying = true;
        }

        /// <summary>
        /// Called each fixed update during playback. Performs checksum verification
        /// and desync detection.
        /// </summary>
        public PlaybackTickResult TickPlayback()
        {
            Assert.That(_isPlaying);

            var result = new PlaybackTickResult();

            if (!_hasDesynced)
            {
                var checksums = _playbackMetadata.Checksums;
                var currentFrame = _world.FixedFrame;

                _log.Trace("Completed playback frame {}", currentFrame);

                if (checksums.TryGetValue(currentFrame, out var expectedChecksum))
                {
                    using var _ = TrecsProfiling.Start("CalculateCurrentChecksum");

                    var actualChecksum = _checksumCalculator.CalculateCurrentChecksum(
                        version: _version,
                        _checksumSerializerHelper,
                        _gameStateSerializer.ChecksumSerializationFlags
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
                        _hasDesynced = true;
                        result.DesyncDetected = true;
                    }
                }
            }

            return result;
        }

        public void EndPlayback()
        {
            Assert.That(_isPlaying);

            var entityInputQueue = _world.GetEntityInputQueue();

            entityInputQueue.RemoveHistoryLocker(this);
            entityInputQueue.ClearFutureInputsAfterOrAt(_world.FixedFrame);

            SetInputsEnabled(true);

            _playbackMetadata = null;
            _isPlaying = false;

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

        public void Dispose()
        {
            Assert.That(!_isPlaying);
            _checksumSerializerHelper.Dispose();
        }
    }
}
