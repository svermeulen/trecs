using System;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public partial class RemoveCleanupHandler : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public RemoveCleanupHandler(World world)
        {
            World = world.CreateAccessor();

            World
                .Events.InGroupsWithTags<FrenzyTags.Fish>()
                .OnRemoved(OnFishRemovedImpl)
                .AddTo(_disposables);

            World
                .Events.InGroupsWithTags<FrenzyTags.Meal>()
                .OnRemoved(OnMealRemovedImpl)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        [ForEachEntity]
        void OnFishRemovedImpl(in TargetMeal targetMeal)
        {
            if (targetMeal.Value.Exists(World))
            {
                World.RemoveEntity(targetMeal.Value);
            }
        }

        [ForEachEntity]
        void OnMealRemovedImpl(in ApproachingFish fish)
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
