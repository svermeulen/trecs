using Trecs.Collections;

namespace Trecs.Serialization
{
    [TypeId(81038975)]
    public class DebugRecordingMetadata
    {
        public DebugRecordingMetadata(
            int startFixedFrame,
            int endFixedFrame,
            DenseDictionary<int, uint> checksums,
            DenseHashSet<BlobId> blobIds
        )
        {
            StartFixedFrame = startFixedFrame;
            EndFixedFrame = endFixedFrame;
            Checksums = checksums;
            BlobIds = blobIds;
        }

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<DenseDictionarySerializer<int, uint>>();
            registry.RegisterSerializer<DenseHashSetSerializer<BlobId>>();
            registry.RegisterSerializer<DebugRecordingMetadata.Serializer>();
        }

        public int StartFixedFrame { get; }
        public int EndFixedFrame { get; }
        public DenseDictionary<int, uint> Checksums { get; }
        public DenseHashSet<BlobId> BlobIds { get; }

        public class Serializer : ISerializer<DebugRecordingMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref DebugRecordingMetadata value, ISerializationReader reader)
            {
                var startFixedFrame = reader.Read<int>("StartFixedFrame");
                var endFixedFrame = reader.Read<int>("EndFixedFrame");
                var checksums = reader.Read<DenseDictionary<int, uint>>("Checksums");
                var blobIds = reader.Read<DenseHashSet<BlobId>>("BlobIds");

                value = new DebugRecordingMetadata(
                    startFixedFrame,
                    endFixedFrame,
                    checksums,
                    blobIds
                );
            }

            public void Serialize(in DebugRecordingMetadata value, ISerializationWriter writer)
            {
                writer.Write<int>("StartFixedFrame", value.StartFixedFrame);
                writer.Write<int>("EndFixedFrame", value.EndFixedFrame);
                writer.Write<DenseDictionary<int, uint>>("Checksums", value.Checksums);
                writer.Write<DenseHashSet<BlobId>>("BlobIds", value.BlobIds);
            }
        }
    }
}
