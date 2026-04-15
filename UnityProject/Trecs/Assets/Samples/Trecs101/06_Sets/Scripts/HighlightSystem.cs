using Unity.Mathematics;

namespace Trecs.Samples.Filters
{
    /// <summary>
    /// Demonstrates adding/removing entities from a persistent set.
    ///
    /// A sine wave sweeps across the grid each frame. Particles inside the
    /// wave band are added to the HighlightedParticle set; particles
    /// outside are removed. This creates a sparse, changing subset that
    /// other systems can iterate efficiently.
    /// </summary>
    public partial class HighlightSystem : ISystem
    {
        readonly int _gridSize;

        public HighlightSystem(int gridSize)
        {
            _gridSize = gridSize;
        }

        public void Execute()
        {
            float waveCenter = math.sin(World.ElapsedTime * 2f) * _gridSize * 0.6f;
            const float waveBandWidth = 2f;

            // Iterate all particles and update set membership based on wave position.
            // Add/Remove are deferred — they queue changes that take effect at the
            // next SubmitEntities boundary, making them safe to call during iteration.
            foreach (var particle in ParticleView.Query(World).WithTags<SampleTags.Particle>())
            {
                float distFromWave = math.abs(particle.Position.x - waveCenter);

                if (distFromWave < waveBandWidth)
                {
                    World.SetAdd<SampleSets.HighlightedParticle>(particle.EntityIndex);
                    particle.Lifetime = World.ElapsedTime;
                }
                else
                {
                    World.SetRemove<SampleSets.HighlightedParticle>(particle.EntityIndex);
                }
            }
        }

        partial struct ParticleView : IAspect, IRead<Position>, IWrite<Lifetime> { }
    }
}
