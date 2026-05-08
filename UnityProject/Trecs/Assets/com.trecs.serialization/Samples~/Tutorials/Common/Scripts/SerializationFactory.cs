namespace Trecs.Serialization.Samples
{
    /// <summary>
    /// Sample helper that constructs the full serialization stack (registry +
    /// world-state serializer + snapshot serializer + bundle serializer +
    /// bundle recorder/player) in one call. Copy into your own project and
    /// adjust as needed — for example, a save-game-only project would skip
    /// the recorder/player entirely.
    /// </summary>
    public static class SerializationFactory
    {
        public static SerializationServices CreateAll(World world)
        {
            var registry = TrecsSerialization.CreateSerializerRegistry();

            var worldStateSerializer = new WorldStateSerializer(world);
            var snapshots = new SnapshotSerializer(worldStateSerializer, registry, world);
            var bundleSerializer = new RecordingBundleSerializer(registry);
            var recorderSettings = new BundleRecorderSettings();
            var recorder = new BundleRecorder(
                world,
                worldStateSerializer,
                registry,
                recorderSettings,
                snapshots
            );
            var player = new BundlePlayer(world, worldStateSerializer, registry, snapshots);

            recorder.Initialize();
            player.Initialize();

            return new SerializationServices(
                registry,
                worldStateSerializer,
                snapshots,
                bundleSerializer,
                recorder,
                player
            );
        }
    }
}
