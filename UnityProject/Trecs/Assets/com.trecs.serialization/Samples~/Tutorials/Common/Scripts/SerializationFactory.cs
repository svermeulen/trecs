namespace Trecs.Serialization.Samples
{
    /// <summary>
    /// Sample helper that constructs the full serialization stack (registry + world state
    /// serializer + snapshot/recording/playback handlers) in one call. Copy into your
    /// own project and adjust as needed — for example, a save-game-only project would
    /// skip the recording handlers entirely.
    /// </summary>
    public static class SerializationFactory
    {
        public static SerializationServices CreateAll(World world)
        {
            var registry = TrecsSerialization.CreateSerializerRegistry();

            var worldStateSerializer = new WorldStateSerializer(world);
            var snapshots = new SnapshotSerializer(worldStateSerializer, registry, world);
            var recorder = new RecordingHandler(worldStateSerializer, registry, world);
            var playback = new PlaybackHandler(worldStateSerializer, snapshots, registry, world);

            return new SerializationServices(
                registry,
                worldStateSerializer,
                snapshots,
                recorder,
                playback
            );
        }
    }
}
