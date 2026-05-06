using Unity.Mathematics;

namespace Trecs.Samples.Sets
{
    /// <summary>
    /// Iterates only the WaveZ set — not every particle in the grid.
    /// Writes a smooth intensity value based on distance from the wave center.
    /// </summary>
    [ExecuteAfter(typeof(WaveMembershipSystem))]
    public partial class WaveZEffectSystem : ISystem
    {
        readonly SampleSettings _settings;
        readonly float _gridExtent;

        public WaveZEffectSystem(SampleSettings settings)
        {
            _settings = settings;
            _gridExtent = settings.GridSize * settings.Spacing * 0.5f;
        }

        [ForEachEntity(typeof(SampleTags.Particle), Set = typeof(SampleSets.WaveZ))]
        void Execute(in WaveZView view)
        {
            float waveCenterZ = math.cos(World.ElapsedTime * _settings.WaveZSpeed) * _gridExtent;
            float dist = math.abs(view.Position.z - waveCenterZ);
            view.CoolIntensity = math.saturate(1f - dist / _settings.WaveBandWidth);
        }

        partial struct WaveZView : IAspect, IRead<Position>, IWrite<CoolIntensity> { }
    }
}
