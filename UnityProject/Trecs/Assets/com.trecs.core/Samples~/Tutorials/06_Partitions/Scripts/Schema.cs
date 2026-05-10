using Unity.Mathematics;

namespace Trecs.Samples.Partitions
{
    public static class BallTags
    {
        public struct Ball : ITag { }

        public struct Active : ITag { }
    }

    [Unwrap]
    public partial struct RestTimer : IEntityComponent
    {
        public float Value;
    }

    [Unwrap]
    public partial struct Velocity : IEntityComponent
    {
        public float3 Value;
    }

    public static partial class SampleTemplates
    {
        /// <summary>
        /// Ball entity with a presence/absence Active partition: balls that have the
        /// Active tag are physics-simulated and stored contiguously for cache-friendly
        /// iteration; balls without it are idle (queryable via
        /// <c>Without = typeof(BallTags.Active)</c>).
        /// </summary>
        public partial class BallEntity
            : ITemplate,
                ITagged<BallTags.Ball>,
                IPartitionedBy<BallTags.Active>
        {
            Position Position;
            Velocity Velocity;
            RestTimer RestTimer;
            GameObjectId GameObjectId;
        }
    }
}
