using Unity.Mathematics;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Fixed-update system: smoothly wanders each character around a box on
    /// the XZ plane using 2D Perlin noise. Trail bookkeeping lives in the
    /// per-variant trail-updater systems so this stays independent of which
    /// collection type backs the trail.
    /// </summary>
    public partial class CharacterMover : ISystem
    {
        readonly SampleSettings _settings;

        public CharacterMover(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(typeof(DynamicCollectionsTags.Character))]
        void Execute(in Character character)
        {
            character.Position = WanderAt(
                character.NoiseOffset,
                (float)World.ElapsedTime,
                _settings.WanderExtent,
                _settings.WanderTimeScale
            );
        }

        partial struct Character : IAspect, IRead<NoiseOffset>, IWrite<Position> { }

        /// <summary>
        /// Smooth pseudo-random position on the XZ plane, bounded to a
        /// (2 * <paramref name="extent"/>) box centred at the origin. Each
        /// character passes its own <paramref name="offset"/> so they wander
        /// independently. Exposed so the scene initializer can place each
        /// character at its t=0 position.
        /// </summary>
        public static float3 WanderAt(float offset, float time, float extent, float timeScale)
        {
            // Large enough that the two axes sample uncorrelated regions of
            // the Perlin field — keeps X and Z motion from rhyming.
            const float axisSeparation = 100f;

            float t = time * timeScale;
            return new float3(
                noise.cnoise(new float2(offset, t)) * extent,
                0.5f,
                noise.cnoise(new float2(offset + axisSeparation, t)) * extent
            );
        }
    }
}
