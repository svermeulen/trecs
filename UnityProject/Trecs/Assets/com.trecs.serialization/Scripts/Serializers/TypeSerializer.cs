using System;

namespace Trecs.Serialization
{
    public class TypeSerializer : ISerializer<Type>
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
