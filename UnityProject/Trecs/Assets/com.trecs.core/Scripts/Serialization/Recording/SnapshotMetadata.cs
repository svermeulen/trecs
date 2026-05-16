using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Header data for a snapshot. Returned by
    /// <see cref="SnapshotSerializer.SaveSnapshot(int, System.IO.Stream, bool)"/>
    /// and <see cref="SnapshotSerializer.LoadSnapshot(System.IO.Stream)"/>,
    /// and readable without restoring full state via
    /// <see cref="SnapshotSerializer.PeekMetadata(System.IO.Stream)"/>.
    /// </summary>
    [TypeId(136305329)]
    public sealed class SnapshotMetadata
    {
        /// <summary>
        /// User-defined schema version written at save time. Trecs does not
        /// interpret this value; it is surfaced so callers can decide whether
        /// a snapshot is compatible with the current world schema.
        /// </summary>
        public int Version { get; init; }

        /// <summary>World fixed-frame at capture time.</summary>
        public int FixedFrame { get; init; }

        /// <summary>Heap blob references the snapshot depends on.</summary>
        public DenseHashSet<BlobId> BlobIds { get; init; } = new();

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer(new Serializer());
        }

        public sealed class Serializer : ISerializer<SnapshotMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref SnapshotMetadata value, ISerializationReader reader)
            {
                var version = reader.Read<int>("Version");
                var blobIds = reader.Read<DenseHashSet<BlobId>>("BlobIds");
                var fixedFrame = reader.Read<int>("FixedFrame");

                value = new SnapshotMetadata
                {
                    Version = version,
                    BlobIds = blobIds,
                    FixedFrame = fixedFrame,
                };
            }

            public void Serialize(in SnapshotMetadata value, ISerializationWriter writer)
            {
                writer.Write("Version", value.Version);
                writer.Write("BlobIds", value.BlobIds);
                writer.Write("FixedFrame", value.FixedFrame);
            }
        }
    }
}
