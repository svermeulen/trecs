namespace Trecs.Samples.JobSystem
{
    public static class SampleTags
    {
        public struct Particle : ITag { }
    }

    public static partial class SampleTemplates
    {
        public partial class ParticleEntity : ITemplate, IHasTags<SampleTags.Particle>
        {
            public Position Position = Position.Default;
            public Velocity Velocity;
        }
    }
}
