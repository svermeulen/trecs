using System;

namespace Trecs.Serialization
{
    public class DeprecatedSerializer<T> : ISerializer<T>
        where T : unmanaged
    {
        public void Serialize(in T value, ISerializationWriter recursiveWriter)
        {
            throw new InvalidOperationException(
                "This serializer is deprecated and should not be used anymore"
            );
        }

        public void Deserialize(ref T value, ISerializationReader recursiveReader)
        {
            throw new InvalidOperationException(
                "This serializer is deprecated and should not be used anymore"
            );
        }
    }
}
