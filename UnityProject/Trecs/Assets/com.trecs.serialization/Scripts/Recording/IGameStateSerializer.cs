using Trecs.Collections;

namespace Trecs.Serialization
{
    public interface IGameStateSerializer
    {
        void StartSerialize(
            int version,
            SerializationBuffer serializerHelper,
            ReadOnlyDenseHashSet<int> serializationFlags,
            bool includeTypeChecks
        );

        void SerializeCurrentState(SerializationBuffer serializerHelper);

        bool StartDeserialize(
            SerializationBuffer serializerHelper,
            ReadOnlyDenseHashSet<int> serializationFlags
        );

        void DeserializeCurrentState(SerializationBuffer serializerHelper);

        ReadOnlyDenseHashSet<int> SerializationFlags { get; }

        ReadOnlyDenseHashSet<int> ChecksumSerializationFlags { get; }
    }
}
