using Trecs.Collections;

namespace Trecs.Serialization
{
    /// <summary>
    /// Header data for a bookmark. Returned by
    /// <see cref="BookmarkSerializer.SaveBookmark(int, System.IO.Stream, bool, int)"/>
    /// and <see cref="BookmarkSerializer.LoadBookmark(System.IO.Stream)"/>,
    /// and readable without restoring full state via
    /// <see cref="BookmarkSerializer.PeekMetadata(System.IO.Stream)"/>.
    /// </summary>
    [TypeId(136305329)]
    public class BookmarkMetadata
    {
        /// <summary>
        /// User-defined schema version written at save time. Trecs does not
        /// interpret this value; it is surfaced so callers can decide whether
        /// a bookmark is compatible with the current world schema.
        /// </summary>
        public int Version;

        /// <summary>World fixed-frame at capture time.</summary>
        public int FixedFrame;

        /// <summary>Number of connected peers when this bookmark was saved (host bookmarks only).</summary>
        public int NumConnections;

        /// <summary>Heap blob references the bookmark depends on.</summary>
        public readonly DenseHashSet<BlobId> BlobIds = new();

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<Serializer>();
        }

        public class Serializer : ISerializer<BookmarkMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref BookmarkMetadata value, ISerializationReader reader)
            {
                value ??= new();
                value.Version = reader.Read<int>("Version");
                reader.ReadInPlace<DenseHashSet<BlobId>>("BlobIds", value.BlobIds);
                value.NumConnections = reader.Read<int>("NumConnections");
                value.FixedFrame = reader.Read<int>("FixedFrame");
            }

            public void Serialize(in BookmarkMetadata value, ISerializationWriter writer)
            {
                writer.Write("Version", value.Version);
                writer.Write("BlobIds", value.BlobIds);
                writer.Write("NumConnections", value.NumConnections);
                writer.Write("FixedFrame", value.FixedFrame);
            }
        }
    }
}
