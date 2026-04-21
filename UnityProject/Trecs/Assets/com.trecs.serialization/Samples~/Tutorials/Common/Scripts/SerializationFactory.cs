namespace Trecs.Serialization.Samples
{
    /// <summary>
    /// Sample helper that constructs the full serialization stack (registry + world state
    /// serializer + bookmark/recording/playback handlers) in one call. Copy into your
    /// own project and adjust as needed — for example, a save-game-only project would
    /// skip the recording handlers entirely.
    /// </summary>
    public static class SerializationFactory
    {
        public static SerializationServices CreateAll(World world)
        {
            var registry = TrecsSerialization.CreateSerializerRegistry();

            var worldStateSerializer = new WorldStateSerializer(world);
            var bookmarks = new BookmarkSerializer(worldStateSerializer, registry, world);
            var recorder = new RecordingHandler(worldStateSerializer, registry, world);
            var playback = new PlaybackHandler(worldStateSerializer, bookmarks, registry, world);

            return new SerializationServices(
                registry,
                worldStateSerializer,
                bookmarks,
                recorder,
                playback
            );
        }
    }
}
