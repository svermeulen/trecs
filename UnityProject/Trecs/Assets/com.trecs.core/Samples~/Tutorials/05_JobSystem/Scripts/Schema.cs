using Unity.Mathematics;

namespace Trecs.Samples.JobSystem
{
    public static class SampleTags
    {
        public struct Particle : ITag { }
    }

    [Unwrap]
    public partial struct Velocity : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct DesiredNumParticles : IEntityComponent
    {
        public int Value;
    }

    [Unwrap]
    public partial struct IsJobsEnabled : IEntityComponent
    {
        public bool Value;
    }

    public static partial class SampleTemplates
    {
        public partial class ParticleEntity
            : ITemplate,
                IExtends<CommonTemplates.IndirectRenderable>,
                ITagged<SampleTags.Particle>
        {
            Velocity Velocity;
        }

        public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            DesiredNumParticles DesiredNumParticles = new() { Value = 5000 };
            IsJobsEnabled IsJobsEnabled = new() { Value = true };
        }
    }
}
