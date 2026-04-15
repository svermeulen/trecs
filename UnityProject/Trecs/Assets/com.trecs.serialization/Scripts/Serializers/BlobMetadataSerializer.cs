namespace Trecs.Serialization
{
    public class BlobMetadataSerializer : ISerializer<BlobMetadata>
    {
        public BlobMetadataSerializer() { }

        public void Deserialize(ref BlobMetadata value, ISerializationReader reader)
        {
            value.Type = reader.Read<System.Type>("Type");
            value.LastAccessTime = reader.Read<long>("LastAccessTime");
            value.NumBytes = reader.Read<long>("NumBytes");
            value.IsNative = reader.Read<bool>("IsNative");
        }

        public void Serialize(in BlobMetadata value, ISerializationWriter writer)
        {
            writer.Write<System.Type>("Type", value.Type);
            writer.Write<long>("LastAccessTime", value.LastAccessTime);
            writer.Write<long>("NumBytes", value.NumBytes);
            writer.Write<bool>("IsNative", value.IsNative);
        }
    }
}
