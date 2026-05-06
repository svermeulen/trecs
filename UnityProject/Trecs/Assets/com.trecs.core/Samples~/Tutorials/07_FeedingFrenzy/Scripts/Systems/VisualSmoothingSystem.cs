using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Smoothly chases the rendered position/rotation toward the
    /// simulation values, making visuals fluid even at low fixed
    /// timestep rates.
    ///
    /// Runs in the Presentation phase before the RendererSystem. Writes to
    /// Position/Rotation (read by RendererSystem) by lerping toward
    /// SimPosition/SimRotation (written by fixed-update systems).
    /// </summary>
    [Phase(SystemPhase.Presentation)]
    public partial class VisualSmoothingSystem : ISystem
    {
        const float ChaseSpeed = 15f;

        [ForEachEntity(typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void Execute(in Fish fish, in NativeWorldAccessor world)
        {
            float t = math.saturate(world.DeltaTime * ChaseSpeed);
            fish.Position = math.lerp(fish.Position, fish.SimPosition, t);
            fish.Rotation = math.slerp(fish.Rotation, fish.SimRotation, t);
        }

        partial struct Fish
            : IAspect,
                IRead<SimPosition, SimRotation>,
                IWrite<Position, Rotation> { }
    }
}
