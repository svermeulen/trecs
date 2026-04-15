using System;

namespace Trecs.Serialization
{
    public class SerializationServices : IDisposable
    {
        public SerializerRegistry Registry { get; }
        public EcsStateSerializer EcsStateSerializer { get; }
        public IGameStateSerializer GameStateSerializer { get; }
        public RecordingChecksumCalculator ChecksumCalculator { get; }
        public BookmarkSerializer BookmarkSerializer { get; }
        public RecordingHandler RecordingHandler { get; }
        public PlaybackHandler PlaybackHandler { get; }

        public SerializationServices(
            SerializerRegistry registry,
            EcsStateSerializer ecsStateSerializer,
            IGameStateSerializer gameStateSerializer,
            RecordingChecksumCalculator checksumCalculator,
            BookmarkSerializer bookmarkSerializer,
            RecordingHandler recordingHandler,
            PlaybackHandler playbackHandler
        )
        {
            Registry = registry;
            EcsStateSerializer = ecsStateSerializer;
            GameStateSerializer = gameStateSerializer;
            ChecksumCalculator = checksumCalculator;
            BookmarkSerializer = bookmarkSerializer;
            RecordingHandler = recordingHandler;
            PlaybackHandler = playbackHandler;
        }

        public void Dispose()
        {
            PlaybackHandler.Dispose();
            RecordingHandler.Dispose();
            BookmarkSerializer.Dispose();
        }
    }
}
