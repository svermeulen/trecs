using Trecs.Collections;

namespace Trecs.Serialization
{
    [TypeId(136305329)]
    public class BookmarkMetadata
    {
        public readonly DenseHashSet<BlobId> BlobIds = new();
        public int NumConnections; // Only applicable for host bookmarks
        public int FixedFrame;

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<BookmarkMetadata.Serializer>();
        }

        /// <summary>
        /// Register all serializers required by the recording/playback subsystem.
        /// Call this from your project's installer when using RecordingHandler,
        /// PlaybackHandler, or BookmarkSerializer.
        /// </summary>
        public static void RegisterAllRecordingSerializers(SerializerRegistry registry)
        {
            RegisterSerializers(registry);
            DebugRecordingMetadata.RegisterSerializers(registry);
        }

        public class Serializer : ISerializer<BookmarkMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref BookmarkMetadata value, ISerializationReader reader)
            {
                value ??= new();
                reader.ReadInPlace<DenseHashSet<BlobId>>("BlobIds", value.BlobIds);
                value.NumConnections = reader.Read<int>("NumConnections");
                value.FixedFrame = reader.Read<int>("FixedFrame");
            }

            public void Serialize(in BookmarkMetadata value, ISerializationWriter writer)
            {
                writer.Write("BlobIds", value.BlobIds);
                writer.Write("NumConnections", value.NumConnections);
                writer.Write("FixedFrame", value.FixedFrame);
            }
        }
    }
}
