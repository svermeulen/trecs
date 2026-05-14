using System;
using Trecs.Collections;

namespace Trecs
{
    /// <summary>
    /// Metadata for a single blob entry in a <see cref="BlobManifest"/>.
    /// </summary>
    [TypeId(378502946)]
    public struct BlobMetadata
    {
        public Type Type;
        public long LastAccessTime;
        public long NumBytes;
        public bool IsNative;

        internal sealed class Serializer : ISerializer<BlobMetadata>
        {
            public void Deserialize(ref BlobMetadata value, ISerializationReader reader)
            {
                value.Type = reader.Read<Type>("Type");
                value.LastAccessTime = reader.Read<long>("LastAccessTime");
                value.NumBytes = reader.Read<long>("NumBytes");
                value.IsNative = reader.Read<bool>("IsNative");
            }

            public void Serialize(in BlobMetadata value, ISerializationWriter writer)
            {
                writer.Write<Type>("Type", value.Type);
                writer.Write<long>("LastAccessTime", value.LastAccessTime);
                writer.Write<long>("NumBytes", value.NumBytes);
                writer.Write<bool>("IsNative", value.IsNative);
            }
        }
    }

    /// <summary>
    /// Index of all known blobs in a <see cref="IBlobStore"/>, mapping
    /// <see cref="BlobId"/> to <see cref="BlobMetadata"/>.
    /// </summary>
    [TypeId(767600239)]
    public sealed class BlobManifest
    {
        public readonly DenseDictionary<BlobId, BlobMetadata> Values = new();

        public static long GetTimeForAccessTime()
        {
            return DateTime.Now.Ticks;
        }

        internal sealed class Serializer : ISerializer<BlobManifest>
        {
            public void Deserialize(ref BlobManifest value, ISerializationReader reader)
            {
                value ??= new();

                SerializationReaderExtensions.ReadInPlace(reader, "Values", value.Values);
            }

            public void Serialize(in BlobManifest value, ISerializationWriter writer)
            {
                writer.Write<DenseDictionary<BlobId, BlobMetadata>>("Values", value.Values);
            }
        }
    }
}
