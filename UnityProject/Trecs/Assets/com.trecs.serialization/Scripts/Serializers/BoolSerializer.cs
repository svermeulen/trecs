namespace Trecs.Serialization
{
    public sealed class BoolSerializer : ISerializer<bool>, ISerializerDelta<bool>
    {
        public BoolSerializer() { }

        public void Serialize(in bool value, ISerializationWriter writer)
        {
            writer.WriteBit(value);
        }

        public void Deserialize(ref bool value, ISerializationReader reader)
        {
            value = reader.ReadBit();
        }

        public void SerializeDelta(in bool value, in bool baseValue, ISerializationWriter writer)
        {
            writer.WriteBit(value);
        }

        public void DeserializeDelta(ref bool value, in bool baseValue, ISerializationReader reader)
        {
            value = reader.ReadBit();
        }
    }
}
