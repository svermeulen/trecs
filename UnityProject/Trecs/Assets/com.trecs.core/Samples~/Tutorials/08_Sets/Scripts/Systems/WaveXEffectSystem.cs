using Unity.Mathematics;

namespace Trecs.Samples.Sets
{
    /// <summary>
    /// Iterates only the WaveX set — not every particle in the grid.
    /// Writes a smooth intensity value based on distance from the wave center.
    /// </summary>
    [ExecuteAfter(typeof(WaveMembershipSystem))]
    public partial class WaveXEffectSystem : ISystem
    {
        readonly SampleSettings _settings;
        readonly float _gridExtent;

        public WaveXEffectSystem(SampleSettings settings)
        {
            _settings = settings;
            _gridExtent = settings.GridSize * settings.Spacing * 0.5f;
        }

        [ForEachEntity(typeof(SampleTags.Particle), Set = typeof(SampleSets.WaveX))]
        void Execute(in WaveXView view)
        {
            float waveCenterX = math.sin(World.ElapsedTime * _settings.WaveXSpeed) * _gridExtent;
            float dist = math.abs(view.Position.x - waveCenterX);
            view.WarmIntensity = math.saturate(1f - dist / _settings.WaveBandWidth);
        }

        partial struct WaveXView : IAspect, IRead<Position>, IWrite<WarmIntensity> { }
    }
}
