using Unity.Mathematics;

namespace Trecs.Samples.Partitions
{
    public static class BallTags
    {
        public struct Ball : ITag { }

        public struct Active : ITag { }

        public struct Resting : ITag { }
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
        /// Ball entity with two partitions: Active (physics simulated) and Resting (idle).
        /// Entities in the Active partition are stored contiguously in memory, so the
        /// physics system iterates them with optimal cache performance.
        /// </summary>
        public partial class BallEntity
            : ITemplate,
                ITagged<BallTags.Ball>,
                IHasPartition<BallTags.Active>,
                IHasPartition<BallTags.Resting>
        {
            Position Position;
            Velocity Velocity;
            RestTimer RestTimer;
            GameObjectId GameObjectId;
        }
    }
}
