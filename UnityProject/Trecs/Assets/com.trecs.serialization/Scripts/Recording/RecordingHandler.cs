using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class RecordingHandler : IInputHistoryLocker, IDisposable
    {
        static readonly TrecsLog _log = new(nameof(RecordingHandler));

        readonly IGameStateSerializer _gameStateSerializer;
        readonly SerializationBuffer _serializerHelper;
        readonly IDisposable _eventSubscription;
        readonly SimpleSubject _checksumRecorded = new();
        readonly RecordingChecksumCalculator _checksumCalculator;
        readonly BlobCache _blobCache;

        WorldAccessor _world;
        bool _isRecording;
        RecordingInfo _recordingInfo;
        int _recordingVersion;

        public RecordingHandler(
            BlobCache blobCache,
            RecordingChecksumCalculator checksumCalculator,
            IGameStateSerializer gameStateSerializer,
            SerializerRegistry serializerManager,
            World world
        )
        {
            _blobCache = blobCache;
            _checksumCalculator = checksumCalculator;
            _gameStateSerializer = gameStateSerializer;
            _serializerHelper = new SerializationBuffer(serializerManager);

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

        public SerializationBuffer SerializerHelper
        {
            get { return _serializerHelper; }
        }

        public void MakeBookmark(int version, bool includeTypeChecks)
        {
            Assert.That(_isRecording);

            _gameStateSerializer.StartSerialize(
                version: version,
                _serializerHelper,
                _gameStateSerializer.SerializationFlags,
                includeTypeChecks: includeTypeChecks
            );

            var bookmarkMetadata = new BookmarkMetadata();

            _blobCache.GetAllActiveBlobIds(bookmarkMetadata.BlobIds);

            _serializerHelper.Write("metadata", bookmarkMetadata);
            _gameStateSerializer.SerializeCurrentState(_serializerHelper);
            var numBytes = _serializerHelper.EndWrite();

            _log.Trace("Recording bookmark ({0.00} kb)", numBytes / 1024f);
        }

        public void StartRecording(int version, bool checksumsEnabled, int checksumFrameInterval)
        {
#if TRECS_IS_PROFILING
            _log.Warning("Recording while profiling is enabled");
#endif
            Assert.That(!_isRecording);
            _isRecording = true;

            Assert.IsNull(_recordingInfo);

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

        public RecordingInfo EndRecording(SerializationBuffer serializerHelper)
        {
            Assert.That(_isRecording);
            Assert.IsNotNull(_recordingInfo);

            var finalFrame = _world.FixedFrame;

            _gameStateSerializer.StartSerialize(
                version: _recordingVersion,
                serializerHelper,
                _gameStateSerializer.SerializationFlags,
                includeTypeChecks: true
            );

            _blobCache.GetAllActiveBlobIds(_recordingInfo.BlobIds);

            long bytesStart = serializerHelper.NumBytesWritten;
            serializerHelper.Write(
                "metadata",
                new DebugRecordingMetadata(
                    startFixedFrame: _recordingInfo.StartFrame,
                    endFixedFrame: finalFrame,
                    checksums: _recordingInfo.Checksums,
                    blobIds: _recordingInfo.BlobIds
                )
            );
            _log.Debug(
                "Serialized metadata ({0.00} kb)",
                (serializerHelper.NumBytesWritten - bytesStart) / 1024f
            );

            var entityInputQueue = _world.GetEntityInputQueue();

            entityInputQueue.Serialize(
                new Trecs.Serialization.TrecsSerializationWriterAdapter(serializerHelper)
            );
            _log.Debug(
                "Serialized EntityInputQueue ({0.00} kb)",
                (serializerHelper.NumBytesWritten - bytesStart) / 1024f
            );

            serializerHelper.Write<int>("recordingSentinel", TrecsConstants.RecordingSentinelValue);
            var recordingNumBytes = serializerHelper.EndWrite();

            serializerHelper.MemoryStream.Position = 0;

            var startFrame = _recordingInfo.StartFrame;
            var numFrames = finalFrame - startFrame;
            _log.Debug(
                "Recording complete ({0.00} kb recording). Recorded {} frames and {} checksums",
                recordingNumBytes / 1024f,
                numFrames,
                _recordingInfo.Checksums.Count
            );

            entityInputQueue.RemoveHistoryLocker(this);

            var recordingInfo = _recordingInfo;
            _recordingInfo = null;
            _isRecording = false;
            return recordingInfo;
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
                        _serializerHelper,
                        _gameStateSerializer.ChecksumSerializationFlags
                    );

                    // Note that we can't do Add here because we sometimes record the client, which can do rollbacks,
                    // so we need to overwrite the checksum in this case
                    _recordingInfo.Checksums[currentFrame] = checksum;

                    _checksumRecorded.Invoke();
                }
            }
#endif
        }

        public void Dispose()
        {
            Assert.That(!_isRecording);
            Assert.IsNull(_recordingInfo);

            _eventSubscription.Dispose();
            _serializerHelper.Dispose();
        }

        public class RecordingInfo
        {
            public int StartFrame;
            public bool ChecksumsEnabled;
            public int ChecksumFrameInterval;
            public DenseDictionary<int, uint> Checksums;
            public DenseHashSet<BlobId> BlobIds;
        }
    }
}
