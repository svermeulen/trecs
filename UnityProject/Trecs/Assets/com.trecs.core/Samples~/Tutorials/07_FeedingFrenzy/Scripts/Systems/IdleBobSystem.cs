using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Bobs idle (NotEating) fish up and down with a sinusoidal wave.
    ///
    /// Demonstrates: [WrapAsJob] with EntityHandle for per-entity variation.
    /// Each fish gets a unique phase offset via the golden ratio so they
    /// bob at different times for visual appeal.
    ///
    /// Writes to SimPosition. The VisualSmoothingSystem chases it.
    /// </summary>
    public partial class IdleBobSystem : ISystem
    {
        const float GoldenRatio = 1.61803f;

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
        [WrapAsJob]
        static void Execute(in Fish fish, EntityHandle handle, in NativeWorldAccessor world)
        {
            float phaseOffset = handle.Id * GoldenRatio;
            float y = 0.3f * fish.UniformScale * math.sin(3f * world.ElapsedTime + phaseOffset);
            var pos = fish.SimPosition;
            pos.y = y;
            fish.SimPosition = pos;
        }

        partial struct Fish : IAspect, IRead<UniformScale>, IWrite<SimPosition> { }
    }
}
