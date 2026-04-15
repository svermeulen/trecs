namespace Trecs.Samples.States
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

    public static partial class SampleTemplates
    {
        /// <summary>
        /// Ball entity with two states: Active (physics simulated) and Resting (idle).
        /// Entities in the Active state are stored contiguously in memory, so the
        /// physics system iterates them with optimal cache performance.
        /// </summary>
        public partial class BallEntity
            : ITemplate,
                IHasTags<BallTags.Ball>,
                IHasState<BallTags.Active>,
                IHasState<BallTags.Resting>
        {
            public Position Position;
            public Velocity Velocity;
            public RestTimer RestTimer;
            public GameObjectId GameObjectId;
        }
    }
}
