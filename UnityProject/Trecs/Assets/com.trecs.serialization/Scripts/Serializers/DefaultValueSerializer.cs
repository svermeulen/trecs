namespace Trecs.Serialization
{
    internal class DefaultValueSerializer<T> : ISerializer<T>
        where T : unmanaged
    {
        public void Serialize(in T value, ISerializationWriter recursiveWriter) { }

        public void Deserialize(ref T value, ISerializationReader recursiveReader)
        {
            value = default(T);
        }
    }
}
