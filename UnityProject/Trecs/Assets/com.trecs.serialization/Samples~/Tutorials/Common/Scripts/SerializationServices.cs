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
        public SnapshotSerializer Snapshots { get; }
        public RecordingBundleSerializer BundleSerializer { get; }
        public BundleRecorder Recorder { get; }
        public BundlePlayer Player { get; }

        public SerializationServices(
            SerializerRegistry registry,
            WorldStateSerializer worldStateSerializer,
            SnapshotSerializer snapshots,
            RecordingBundleSerializer bundleSerializer,
            BundleRecorder recorder,
            BundlePlayer player
        )
        {
            Registry = registry;
            WorldStateSerializer = worldStateSerializer;
            Snapshots = snapshots;
            BundleSerializer = bundleSerializer;
            Recorder = recorder;
            Player = player;
        }

        public void Dispose()
        {
            Player.Dispose();
            Recorder.Dispose();
            BundleSerializer.Dispose();
            Snapshots.Dispose();
        }
    }
}
