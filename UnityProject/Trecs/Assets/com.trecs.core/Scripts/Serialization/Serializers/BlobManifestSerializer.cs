using Trecs.Collections;

namespace Trecs.Internal
{
    public sealed class BlobManifestSerializer : ISerializer<BlobManifest>
    {
        public BlobManifestSerializer() { }

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
