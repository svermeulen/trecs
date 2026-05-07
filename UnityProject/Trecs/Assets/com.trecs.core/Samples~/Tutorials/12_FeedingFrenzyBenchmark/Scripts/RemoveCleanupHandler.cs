using System;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public partial class RemoveCleanupHandler : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public RemoveCleanupHandler(World world)
        {
            World = world.CreateAccessor(AccessorRole.Fixed);

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
