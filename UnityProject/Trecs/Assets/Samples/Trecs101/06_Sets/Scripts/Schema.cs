namespace Trecs.Samples.Filters
{
    public static class SampleTags
    {
        public struct Particle : ITag { }
    }

    public static class SampleSets
    {
        /// <summary>
        /// Tracks particles that are currently highlighted.
        /// Filters let you iterate a sparse subset of entities within a group
        /// without checking every entity.
        /// </summary>
        public struct HighlightedParticle : IEntitySet<SampleTags.Particle> { }
    }

    public static partial class SampleTemplates
    {
        public partial class ParticleEntity : ITemplate, IHasTags<SampleTags.Particle>
        {
            public Position Position = Position.Default;
            public Lifetime Lifetime;
            public GameObjectId GameObjectId;
        }
    }
}
