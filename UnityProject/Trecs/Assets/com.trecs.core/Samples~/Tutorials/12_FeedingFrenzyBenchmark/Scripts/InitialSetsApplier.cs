using System;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public partial class InitialSetsApplier : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public InitialSetsApplier(FrenzyConfigSettings config, World world)
        {
            World = world.CreateAccessor(AccessorRole.Fixed);

            if (config.SubsetApproach != FrenzySubsetApproach.Sets)
            {
                return;
            }

            World
                .Events.EntitiesWithTags<FrenzyTags.Fish>()
                .OnAdded(OnFishAdded)
                .AddTo(_disposables);

            World
                .Events.EntitiesWithTags<FrenzyTags.Meal>()
                .OnAdded(OnMealAdded)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        [ForEachEntity]
        void OnFishAdded(EntityHandle handle)
        {
            World.Set<FrenzySets.NotEating>().DeferredAdd(handle);
        }

        [ForEachEntity]
        void OnMealAdded(EntityHandle handle)
        {
            World.Set<FrenzySets.NotEating>().DeferredAdd(handle);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
