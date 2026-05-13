using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization.Samples
{
    public partial struct GameObjectId : IEntityComponent
    {
        public int Value;
    }

    [Unwrap]
    public partial struct PrefabId : IEntityComponent
    {
        public int Value;
    }

    [Unwrap]
    public partial struct Position : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct Rotation : IEntityComponent
    {
        public quaternion Value;
    }

    [Unwrap]
    public partial struct UniformScale : IEntityComponent
    {
        public float Value;
    }

    /// <summary>
    /// Per-entity RGBA color for renderers.
    /// </summary>
    [Unwrap]
    public partial struct ColorComponent : IEntityComponent
    {
        public Color Value;
    }
}
