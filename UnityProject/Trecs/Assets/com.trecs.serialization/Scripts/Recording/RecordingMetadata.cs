using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Header data for a recording: the frame range covered, per-frame
    /// checksums used for desync detection during playback, and the heap
    /// blob references the recording depends on.
    /// </summary>
    [TypeId(81038975)]
    public class RecordingMetadata
    {
        public RecordingMetadata(
            int version,
            int startFixedFrame,
            int endFixedFrame,
            long checksumFlags,
            DenseDictionary<int, uint> checksums,
            DenseHashSet<BlobId> blobIds
        )
        {
            Version = version;
            StartFixedFrame = startFixedFrame;
            EndFixedFrame = endFixedFrame;
            ChecksumFlags = checksumFlags;
            Checksums = checksums;
            BlobIds = blobIds;
        }

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<DenseDictionarySerializer<int, uint>>();
            registry.RegisterSerializer<DenseHashSetSerializer<BlobId>>();
            registry.RegisterSerializer<Serializer>();
        }

        /// <summary>
        /// User-defined schema version passed to <see cref="RecordingHandler.StartRecording"/>.
        /// </summary>
        public int Version { get; }

        public int StartFixedFrame { get; }
        public int EndFixedFrame { get; }

        /// <summary>
        /// Serialization flags used when computing per-frame checksums during
        /// recording. Playback must recompute checksums with the same flags,
        /// which <see cref="PlaybackHandler"/> does automatically by reading
        /// this value out of the metadata.
        /// </summary>
        public long ChecksumFlags { get; }

        public DenseDictionary<int, uint> Checksums { get; }
        public DenseHashSet<BlobId> BlobIds { get; }

        public class Serializer : ISerializer<RecordingMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref RecordingMetadata value, ISerializationReader reader)
            {
                var version = reader.Read<int>("Version");
                var startFixedFrame = reader.Read<int>("StartFixedFrame");
                var endFixedFrame = reader.Read<int>("EndFixedFrame");
                var checksumFlags = reader.Read<long>("ChecksumFlags");
                var checksums = reader.Read<DenseDictionary<int, uint>>("Checksums");
                var blobIds = reader.Read<DenseHashSet<BlobId>>("BlobIds");

                value = new RecordingMetadata(
                    version,
                    startFixedFrame,
                    endFixedFrame,
                    checksumFlags,
                    checksums,
                    blobIds
                );
            }

            public void Serialize(in RecordingMetadata value, ISerializationWriter writer)
            {
                writer.Write<int>("Version", value.Version);
                writer.Write<int>("StartFixedFrame", value.StartFixedFrame);
                writer.Write<int>("EndFixedFrame", value.EndFixedFrame);
                writer.Write<long>("ChecksumFlags", value.ChecksumFlags);
                writer.Write<DenseDictionary<int, uint>>("Checksums", value.Checksums);
                writer.Write<DenseHashSet<BlobId>>("BlobIds", value.BlobIds);
            }
        }
    }
}
