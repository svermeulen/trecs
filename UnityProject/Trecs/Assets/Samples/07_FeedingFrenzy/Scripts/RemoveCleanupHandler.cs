using System;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Cleans up cross-entity references when entities are destroyed.
    ///
    /// When a fish is removed (starvation or scaling down), its target
    /// meal is also removed to prevent orphaned meals stuck in the
    /// Eating partition.
    ///
    /// Uses [ForEachEntity] on the OnRemoved callback, which generates
    /// code that correctly accesses entity data during removal callbacks
    /// (removed entities are kept at the end of the backing array, past
    /// the active count).
    /// </summary>
    public partial class RemoveCleanupHandler : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public RemoveCleanupHandler(World world)
        {
            World = world.CreateAccessor();

            World
                .Events.EntitiesWithTags<FrenzyTags.Fish>()
                .OnRemoved(OnFishRemoved)
                .AddTo(_disposables);

            World
                .Events.EntitiesWithTags<FrenzyTags.Meal>()
                .OnRemoved(OnMealRemoved)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        [ForEachEntity]
        void OnFishRemoved(in TargetMeal targetMeal)
        {
            if (targetMeal.Value.Exists(World))
            {
                World.RemoveEntity(targetMeal.Value);
            }
        }

        [ForEachEntity]
        void OnMealRemoved(in ApproachingFish fish)
        {
            if (fish.Value.Exists(World))
            {
                World.RemoveEntity(fish.Value);
            }
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
