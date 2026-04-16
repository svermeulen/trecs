using Unity.Mathematics;

namespace Trecs.Samples.Sets
{
    /// <summary>
    /// Manages membership in the WaveX and WaveZ sets each frame.
    ///
    /// Two sine waves sweep the grid along perpendicular axes. Particles
    /// inside a wave band are added to that wave's set; particles outside
    /// are removed. This is the only system that iterates all particles —
    /// the downstream effect systems iterate only their wave's subset.
    /// </summary>
    public partial class WaveMembershipSystem : ISystem
    {
        readonly SampleSettings _settings;
        readonly float _gridExtent;

        public WaveMembershipSystem(SampleSettings settings)
        {
            _settings = settings;
            _gridExtent = settings.GridSize * settings.Spacing * 0.5f;
        }

        public void Execute()
        {
            float waveCenterX = math.sin(World.ElapsedTime * _settings.WaveXSpeed) * _gridExtent;
            float waveCenterZ = math.cos(World.ElapsedTime * _settings.WaveZSpeed) * _gridExtent;

            foreach (var particle in ParticleView.Query(World).WithTags<SampleTags.Particle>())
            {
                // Reset intensities — downstream effect systems will overwrite
                // for particles that are in their set.
                particle.WarmIntensity = 0;
                particle.CoolIntensity = 0;

                float distX = math.abs(particle.Position.x - waveCenterX);
                float distZ = math.abs(particle.Position.z - waveCenterZ);

                if (distX < _settings.WaveBandWidth)
                    World.SetAdd<SampleSets.WaveX>(particle.EntityIndex);
                else
                    World.SetRemove<SampleSets.WaveX>(particle.EntityIndex);

                if (distZ < _settings.WaveBandWidth)
                    World.SetAdd<SampleSets.WaveZ>(particle.EntityIndex);
                else
                    World.SetRemove<SampleSets.WaveZ>(particle.EntityIndex);
            }
        }

        partial struct ParticleView
            : IAspect,
                IRead<Position>,
                IWrite<WarmIntensity, CoolIntensity> { }
    }
}
