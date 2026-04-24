using Unity.Mathematics;

namespace Trecs.Samples.ReactiveEvents
{
    public static class SampleTags
    {
        public struct Bubble : ITag { }
    }

    [Unwrap]
    public partial struct Velocity : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct Lifetime : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class BubbleEntity : ITemplate, IHasTags<SampleTags.Bubble>
        {
            public Position Position;
            public Velocity Velocity;
            public Lifetime Lifetime;
            public GameObjectId GameObjectId;
        }
    }
}
