namespace Trecs.Serialization
{
    public sealed class StringSerializer : ISerializer<string>
    {
        public StringSerializer() { }

        public void Serialize(in string value, ISerializationWriter writer)
        {
            writer.WriteString("value", value);
        }

        public void Deserialize(ref string value, ISerializationReader reader)
        {
            value = reader.ReadString("value");
        }
    }
}
