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
        void OnFishAdded(EntityIndex entityIndex)
        {
            World.Set<FrenzySets.NotEating>().Defer.Add(entityIndex);
        }

        [ForEachEntity]
        void OnMealAdded(EntityIndex entityIndex)
        {
            World.Set<FrenzySets.NotEating>().Defer.Add(entityIndex);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
