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
        public partial class ParticleEntity : ITemplate, ITagged<SampleTags.Particle>
        {
            Position Position = default;
            WarmIntensity WarmIntensity = default;
            CoolIntensity CoolIntensity = default;
            GameObjectId GameObjectId;
        }
    }
}
