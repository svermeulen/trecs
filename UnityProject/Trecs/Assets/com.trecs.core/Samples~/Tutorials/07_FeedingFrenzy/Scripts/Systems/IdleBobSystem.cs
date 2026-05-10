using Unity.Mathematics;
using Trecs.Internal;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Bobs idle (NotEating) fish up and down with a sinusoidal wave.
    ///
    /// Demonstrates: [WrapAsJob] with EntityIndex for per-entity variation.
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
        static void Execute(in Fish fish, EntityIndex entityIndex, in NativeWorldAccessor world)
        {
            float phaseOffset = entityIndex.Index * GoldenRatio;
            float y = 0.3f * fish.UniformScale * math.sin(3f * world.ElapsedTime + phaseOffset);
            var pos = fish.SimPosition;
            pos.y = y;
            fish.SimPosition = pos;
        }

        partial struct Fish : IAspect, IRead<UniformScale>, IWrite<SimPosition> { }
    }
}
