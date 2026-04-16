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
                IHasTags<BallTags.Ball>,
                IHasPartition<BallTags.Active>,
                IHasPartition<BallTags.Resting>
        {
            public Position Position;
            public Velocity Velocity;
            public RestTimer RestTimer;
            public GameObjectId GameObjectId;
        }
    }
}
