using Trecs.Collections;

namespace Trecs.Serialization
{
    /// <summary>
    /// Header data for a bookmark. Returned by
    /// <see cref="BookmarkSerializer.SaveBookmark(int, System.IO.Stream, bool)"/>
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
        public int Version { get; init; }

        /// <summary>World fixed-frame at capture time.</summary>
        public int FixedFrame { get; init; }

        /// <summary>Heap blob references the bookmark depends on.</summary>
        public DenseHashSet<BlobId> BlobIds { get; init; } = new();

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer<Serializer>();
        }

        public class Serializer : ISerializer<BookmarkMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref BookmarkMetadata value, ISerializationReader reader)
            {
                var version = reader.Read<int>("Version");
                var blobIds = reader.Read<DenseHashSet<BlobId>>("BlobIds");
                var fixedFrame = reader.Read<int>("FixedFrame");

                value = new BookmarkMetadata
                {
                    Version = version,
                    BlobIds = blobIds,
                    FixedFrame = fixedFrame,
                };
            }

            public void Serialize(in BookmarkMetadata value, ISerializationWriter writer)
            {
                writer.Write("Version", value.Version);
                writer.Write("BlobIds", value.BlobIds);
                writer.Write("FixedFrame", value.FixedFrame);
            }
        }
    }
}
