namespace Trecs.Serialization
{
    public class BlitSerializer<T> : ISerializer<T>, ISerializerDelta<T>
        where T : unmanaged
    {
        public BlitSerializer() { }

        public void Deserialize(ref T value, ISerializationReader reader)
        {
            reader.BlitRead("value", ref value);
        }

        public void Serialize(in T value, ISerializationWriter writer)
        {
            writer.BlitWrite("value", value);
        }

        public void DeserializeDelta(ref T value, in T baseValue, ISerializationReader reader)
        {
            reader.BlitReadDelta("value", ref value, baseValue);
        }

        public void SerializeDelta(in T value, in T baseValue, ISerializationWriter writer)
        {
            writer.BlitWriteDelta("value", value, baseValue);
        }
    }
}
