using Trecs.Internal;

namespace Trecs
{
    public interface ISerializer
    {
        // Need object versions since sometimes we want to deserialize as an
        // object, or an interface, rather than a specific type
        void SerializeObject(object value, ISerializationWriter writer);
        void DeserializeObject(ref object value, ISerializationReader reader);
    }

    public interface ISerializer<T> : ISerializer
    {
        void Serialize(in T value, ISerializationWriter writer);
        void Deserialize(ref T value, ISerializationReader reader);

        void ISerializer.DeserializeObject(ref object value, ISerializationReader reader)
        {
            // Needs to work with both value and non value types
            T typedValue = value == null ? default : (T)value;
            Deserialize(ref typedValue, reader);
            value = typedValue;
        }

        void ISerializer.SerializeObject(object value, ISerializationWriter writer)
        {
            TrecsAssert.That(value != null);
            Serialize((T)value, writer);
        }
    }

    public interface ISerializerDelta
    {
        // Need object versions since sometimes we want to deserialize as an
        // object, or an interface, rather than a specific type
        void SerializeObjectDelta(object value, object baseValue, ISerializationWriter writer);
        void DeserializeObjectDelta(
            ref object value,
            object baseValue,
            ISerializationReader reader
        );
    }

    public interface ISerializerDelta<T> : ISerializerDelta
    {
        void SerializeDelta(in T value, in T baseValue, ISerializationWriter writer);
        void DeserializeDelta(ref T value, in T baseValue, ISerializationReader reader);

        void ISerializerDelta.DeserializeObjectDelta(
            ref object value,
            object baseValue,
            ISerializationReader reader
        )
        {
            // Needs to work with both value and non value types
            T typedValue = value == null ? default : (T)value;
            T typedBaseValue = baseValue == null ? default : (T)baseValue;
            DeserializeDelta(ref typedValue, typedBaseValue, reader);
            value = typedValue;
        }

        void ISerializerDelta.SerializeObjectDelta(
            object value,
            object baseValue,
            ISerializationWriter writer
        )
        {
            TrecsAssert.That(value != null);

            T typedValue = (T)value;
            T typedBaseValue = baseValue == null ? default : (T)baseValue;

            SerializeDelta(typedValue, typedBaseValue, writer);
        }
    }
}
