using Unity.Mathematics;

namespace Trecs.Samples
{
    [Unwrap]
    public partial struct GameObjectId : IEntityComponent
    {
        public int Value;

        public static readonly GameObjectId Default = default;
    }

    [Unwrap]
    public partial struct Position : IEntityComponent
    {
        public float3 Value;

        public static Position Default => new(float3.zero);
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
        public static readonly UniformScale Default = new(1f);
    }

    [Unwrap]
    public partial struct Lifetime : IEntityComponent
    {
        public float Value;
    }

    [Unwrap]
    public partial struct Velocity : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct Speed : IEntityComponent
    {
        public float Value;
    }

    /// <summary>
    /// Per-entity RGBA color for GPU indirect rendering.
    /// Multiplied with the material's base color in the shader.
    /// </summary>
    [Unwrap]
    public partial struct ColorComponent : IEntityComponent
    {
        public UnityEngine.Color Value;
        public static readonly ColorComponent Default = new(UnityEngine.Color.white);
    }
}
