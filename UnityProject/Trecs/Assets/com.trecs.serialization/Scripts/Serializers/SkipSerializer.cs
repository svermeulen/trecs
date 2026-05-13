namespace Trecs.Serialization.Internal
{
    internal sealed class SkipSerializer<T> : ISerializer<T>
    {
        public SkipSerializer() { }

        public void Serialize(in T value, ISerializationWriter writer)
        {
            // do nothing
        }

        public void Deserialize(ref T value, ISerializationReader reader)
        {
            // do nothing
        }
    }
}
