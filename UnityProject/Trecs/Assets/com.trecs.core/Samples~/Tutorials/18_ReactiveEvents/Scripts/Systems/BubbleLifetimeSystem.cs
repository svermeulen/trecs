using Trecs.Internal;

namespace Trecs.Samples.ReactiveEvents
{
    /// <summary>
    /// Removes bubbles whose lifetime has run out. The removal is observed by
    /// <see cref="GameStatsUpdater"/>, which cleans up the bubble's
    /// GameObject and increments the removed-event counter.
    /// </summary>
    public partial class BubbleLifetimeSystem : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Bubble))]
        void Execute(EntityIndex entity, ref Lifetime lifetime)
        {
            lifetime.Value -= World.DeltaTime;
            if (lifetime.Value <= 0f)
            {
                World.RemoveEntity(entity);
            }
        }
    }
}
