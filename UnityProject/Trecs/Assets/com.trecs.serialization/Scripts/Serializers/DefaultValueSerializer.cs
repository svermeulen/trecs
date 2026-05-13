namespace Trecs.Serialization.Internal
{
    internal sealed class DefaultValueSerializer<T> : ISerializer<T>
        where T : unmanaged
    {
        public void Serialize(in T value, ISerializationWriter writer) { }

        public void Deserialize(ref T value, ISerializationReader reader)
        {
            value = default(T);
        }
    }
}
