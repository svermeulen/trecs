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
        public partial class BubbleEntity : ITemplate, ITagged<SampleTags.Bubble>
        {
            Position Position;
            Velocity Velocity;
            Lifetime Lifetime;
            GameObjectId GameObjectId;
        }

        public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            BubbleSpawnerSystem.State BubbleSpawnerState = default;
            GameStats GameStats = default;
        }
    }
}
