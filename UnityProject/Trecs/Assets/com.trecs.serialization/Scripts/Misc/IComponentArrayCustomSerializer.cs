using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Override hook for serializing a single component array. Register via
    /// <see cref="WorldStateSerializer.RegisterCustomComponentSerializer{T}"/>.
    /// </summary>
    public interface IComponentArrayCustomSerializer
    {
        void Serialize(IComponentArray array, ISerializationWriter writer);
        void Deserialize(IComponentArray array, ISerializationReader reader);
    }
}
