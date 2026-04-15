namespace Trecs.Serialization
{
    public class SerializableByteArraySerializer : ISerializer<SerializableByteArray>
    {
        public void Serialize(in SerializableByteArray value, ISerializationWriter writer)
        {
            writer.WriteBinary("value", value);
        }

        public void Deserialize(ref SerializableByteArray value, ISerializationReader reader)
        {
            reader.ReadBinary("value", ref value);
        }
    }
}
