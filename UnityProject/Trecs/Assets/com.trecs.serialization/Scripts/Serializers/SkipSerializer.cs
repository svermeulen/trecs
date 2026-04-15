namespace Trecs.Serialization
{
    public class SkipSerializer<T> : ISerializer<T>
    {
        public SkipSerializer() { }

        public void Serialize(in T value, ISerializationWriter recursiveWriter)
        {
            // do nothing
        }

        public void Deserialize(ref T value, ISerializationReader recursiveReader)
        {
            // do nothing
        }
    }
}
