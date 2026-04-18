using Unity.Mathematics;

namespace Trecs.Samples
{
    public partial struct GameObjectId : IEntityComponent
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
    /// Per-entity RGBA color for GPU instanced rendering.
    /// Multiplied with the material's base color in the shader.
    /// </summary>
    [Unwrap]
    public partial struct ColorComponent : IEntityComponent
    {
        public UnityEngine.Color Value;
    }
}
