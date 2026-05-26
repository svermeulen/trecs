using Unity.Mathematics;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Fixed-update system: drives each character around the XZ plane via
    /// 2D Perlin noise. Position.Y is intentionally left alone here — the
    /// per-flavor HeightmapFollower system reads the shared blob and writes
    /// the surface height back into Position.Y. Keeps the "what's the
    /// terrain telling us" logic isolated from the "where would I want to
    /// walk" logic.
    /// </summary>
    public partial class CharacterMover : ISystem
    {
        readonly SampleSettings _settings;

        public CharacterMover(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(typeof(SampleTags.Character))]
        void Execute(in NoiseOffset offset, ref Position position)
        {
            float half = _settings.HeightmapWorldSize * 0.5f;
            float t = (float)World.ElapsedTime * _settings.WanderTimeScale;

            // Large axis-separation constant so the two axes sample
            // uncorrelated regions of the noise field — keeps X and Z
            // motion from rhyming.
            const float axisSeparation = 100f;

            float x = noise.cnoise(new float2(offset.Value, t)) * half;
            float z = noise.cnoise(new float2(offset.Value + axisSeparation, t)) * half;

            position.Value = new float3(x, position.Value.y, z);
        }
    }
}
