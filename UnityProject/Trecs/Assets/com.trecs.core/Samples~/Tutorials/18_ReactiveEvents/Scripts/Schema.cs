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

    public partial struct GameStats : IEntityComponent
    {
        public int AliveCount;
        public int TotalSpawned;
        public int TotalRemoved;
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

        public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            public BubbleSpawnerSystem.State BubbleSpawnerState = default;
            public GameStats GameStats = default;
        }
    }
}
