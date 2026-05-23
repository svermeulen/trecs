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

    // -1 for halve, +1 for double, 0 for no change this frame.
    [Unwrap]
    public partial struct ParticleCountAdjustInput : IEntityComponent
    {
        public int Direction;
    }

    [Unwrap]
    public partial struct ToggleJobsInput : IEntityComponent
    {
        public bool Toggle;
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

            [Input(MissingInputBehavior.Reset)]
            ParticleCountAdjustInput ParticleCountAdjustInput = default;

            [Input(MissingInputBehavior.Reset)]
            ToggleJobsInput ToggleJobsInput = default;
        }
    }
}
