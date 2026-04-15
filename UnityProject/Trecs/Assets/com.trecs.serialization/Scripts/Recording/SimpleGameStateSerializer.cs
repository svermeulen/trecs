using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Simple IGameStateSerializer implementation that wraps EcsStateSerializer.
    /// Suitable for projects that only have ECS state (no Lua, no static seed).
    /// For projects with additional state (e.g. Lua scripting), implement
    /// IGameStateSerializer directly.
    /// </summary>
    public class SimpleGameStateSerializer : IGameStateSerializer
    {
        static readonly TrecsLog _log = new(nameof(SimpleGameStateSerializer));

        readonly EcsStateSerializer _trecsSerializer;

        static readonly DenseHashSet<int> _emptyFlags = new();
        static readonly ReadOnlyDenseHashSet<int> _readOnlyEmptyFlags = new(_emptyFlags);

        public SimpleGameStateSerializer(EcsStateSerializer trecsSerializer)
        {
            _trecsSerializer = trecsSerializer;
        }

        public ReadOnlyDenseHashSet<int> SerializationFlags
        {
            get { return _readOnlyEmptyFlags; }
        }

        public ReadOnlyDenseHashSet<int> ChecksumSerializationFlags
        {
            get { return _readOnlyEmptyFlags; }
        }

        public void StartSerialize(
            int version,
            SerializationBuffer serializerHelper,
            ReadOnlyDenseHashSet<int> serializationFlags,
            bool includeTypeChecks
        )
        {
            serializerHelper.ClearMemoryStream();
            serializerHelper.StartWrite(
                version: version,
                includeTypeChecks: includeTypeChecks,
                serializationFlags
            );
        }

        public void SerializeCurrentState(SerializationBuffer serializerHelper)
        {
            using (TrecsProfiling.Start("SerializeCurrentState"))
            {
                _trecsSerializer.SerializeState(serializerHelper);
            }
        }

        public bool StartDeserialize(
            SerializationBuffer serializerHelper,
            ReadOnlyDenseHashSet<int> serializationFlags
        )
        {
            serializerHelper.ResetMemoryPosition();
            serializerHelper.StartRead(serializationFlags);
            return true;
        }

        public void DeserializeCurrentState(SerializationBuffer serializerHelper)
        {
            _trecsSerializer.DeserializeState(serializerHelper);
        }
    }
}
