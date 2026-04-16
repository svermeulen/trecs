using System;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public partial class InitialSetsApplier : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public InitialSetsApplier(FrenzyConfigSettings config, World world)
        {
            World = world.CreateAccessor();

            if (config.SubsetApproach != FrenzySubsetApproach.Sets)
            {
                return;
            }

            World
                .Events.InGroupsWithTags<FrenzyTags.Fish>()
                .OnAdded(OnFishAdded)
                .AddTo(_disposables);

            World
                .Events.InGroupsWithTags<FrenzyTags.Meal>()
                .OnAdded(OnMealAdded)
                .AddTo(_disposables);
        }

        WorldAccessor World { get; }

        [ForEachEntity]
        void OnFishAdded(EntityIndex entityIndex)
        {
            World.SetAdd<FrenzySets.NotEating>(entityIndex);
        }

        [ForEachEntity]
        void OnMealAdded(EntityIndex entityIndex)
        {
            World.SetAdd<FrenzySets.NotEating>(entityIndex);
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
