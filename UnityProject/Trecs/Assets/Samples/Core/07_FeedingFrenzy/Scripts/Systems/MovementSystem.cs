namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Moves eating fish toward their target meal.
    ///
    /// Demonstrates: [WrapAsJob] with an aspect — the cleanest pattern
    /// for parallel iteration. The source generator creates a Burst-compiled
    /// job struct behind the scenes from this single static method.
    ///
    /// Writes to SimPosition (simulation position). The VisualSmoothingSystem
    /// smoothly chases Position toward SimPosition for rendering.
    /// </summary>
    public partial class MovementSystem : ISystem
    {
        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating) })]
        [WrapAsJob]
        static void Execute(in Fish fish, in NativeWorldAccessor world)
        {
            fish.SimPosition += world.DeltaTime * fish.Velocity;
        }

        partial struct Fish : IAspect, IRead<Velocity>, IWrite<SimPosition> { }
    }
}
