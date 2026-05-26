using System;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="Type"/> references. Round-trips via
    /// <see cref="TypeId"/>'s integer ID.
    /// </summary>
    public sealed class TypeSerializer : ISerializer<Type>
    {
        public TypeSerializer() { }

        public void Deserialize(ref Type value, ISerializationReader reader)
        {
            value = reader.ReadTypeId("Value");
        }

        public void Serialize(in Type value, ISerializationWriter writer)
        {
            writer.WriteTypeId("Value", value);
        }
    }
}
