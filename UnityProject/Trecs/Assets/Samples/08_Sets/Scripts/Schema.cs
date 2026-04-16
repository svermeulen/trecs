namespace Trecs.Samples.Sets
{
    public static class SampleTags
    {
        public struct Particle : ITag { }
    }

    public static class SampleSets
    {
        /// <summary>
        /// Particles inside the horizontal sine wave band.
        /// </summary>
        public struct WaveX : IEntitySet { }

        /// <summary>
        /// Particles inside the vertical sine wave band.
        /// </summary>
        public struct WaveZ : IEntitySet { }
    }

    [Unwrap]
    public partial struct WarmIntensity : IEntityComponent
    {
        public float Value;
    }

    [Unwrap]
    public partial struct CoolIntensity : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class ParticleEntity : ITemplate, IHasTags<SampleTags.Particle>
        {
            public Position Position = default;
            public WarmIntensity WarmIntensity = default;
            public CoolIntensity CoolIntensity = default;
            public GameObjectId GameObjectId;
        }
    }
}
