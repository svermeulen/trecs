using System;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Serializer for <see cref="Type"/> references. Round-trips via
    /// <c>TypeIdProvider</c>'s integer ID, so types referenced this way must
    /// be registered with the same provider on both write and read sides.
    /// </summary>
    public sealed class TypeSerializer : ISerializer<Type>
    {
        public TypeSerializer() { }

        public void Deserialize(ref Type value, ISerializationReader reader)
        {
            value = reader.ReadTypeId("value");
        }

        public void Serialize(in Type value, ISerializationWriter writer)
        {
            writer.WriteTypeId("value", value);
        }
    }
}
