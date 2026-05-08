using Unity.Mathematics;

namespace Trecs.Samples.Aspects
{
    public static class SampleTags
    {
        public struct Boid : ITag { }
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

    public static partial class SampleTemplates
    {
        public partial class BoidEntity : ITemplate, ITagged<SampleTags.Boid>
        {
            Position Position = default;
            Velocity Velocity;
            Speed Speed;
            GameObjectId GameObjectId;
        }
    }
}
