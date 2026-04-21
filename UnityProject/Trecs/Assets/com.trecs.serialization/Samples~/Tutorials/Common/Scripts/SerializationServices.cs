using System;

namespace Trecs.Serialization.Samples
{
    /// <summary>
    /// Sample-side bundle of all serialization handlers wired together.
    /// Convenient for sample composition roots; not part of the public Trecs.Serialization API.
    /// Copy <see cref="SerializationFactory.CreateAll"/> into your project and adjust to taste.
    /// </summary>
    public sealed class SerializationServices : IDisposable
    {
        public SerializerRegistry Registry { get; }
        public WorldStateSerializer WorldStateSerializer { get; }
        public BookmarkSerializer Bookmarks { get; }
        public RecordingHandler Recorder { get; }
        public PlaybackHandler Playback { get; }

        public SerializationServices(
            SerializerRegistry registry,
            WorldStateSerializer worldStateSerializer,
            BookmarkSerializer bookmarks,
            RecordingHandler recorder,
            PlaybackHandler playback
        )
        {
            Registry = registry;
            WorldStateSerializer = worldStateSerializer;
            Bookmarks = bookmarks;
            Recorder = recorder;
            Playback = playback;
        }

        public void Dispose()
        {
            Playback.Dispose();
            Recorder.Dispose();
            Bookmarks.Dispose();
        }
    }
}
