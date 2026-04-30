using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [ExecuteAfter(typeof(FishAdderAndRemover))]
    public partial class MealAdderAndRemover : IManageMealCount, ISystem
    {
        readonly CommonSettings _settings;

        public MealAdderAndRemover(CommonSettings settings)
        {
            _settings = settings;
        }

        public void Execute()
        {
            int maxMeals = World.GlobalComponent<DesiredMealCount>().Read.Value;
            var currentCount = World.CountEntitiesWithTags(TagSet<FrenzyTags.Meal>.Value);
            var config = World.GlobalComponent<FrenzyConfig>().Read;

            if (currentCount < maxMeals)
            {
                SpawnMeals(maxMeals - currentCount, config);
            }
            else if (currentCount > maxMeals)
            {
                DespawnMeals(currentCount - maxMeals, config);
            }
        }

        void SpawnMeals(int count, in FrenzyConfig config)
        {
            bool useJobs =
                config.IterationStyle
                is IterationStyle.ForEachMethodAspectJob
                    or IterationStyle.ForEachMethodComponentsJob
                    or IterationStyle.WrapAsJobAspect
                    or IterationStyle.WrapAsJobComponents
                    or IterationStyle.RawComponentBuffersJob;

            var initialTags =
                config.SubsetApproach is FrenzySubsetApproach.Partitions
                    ? TagSet<FrenzyTags.Meal, FrenzyTags.NotEating>.Value
                    : TagSet<FrenzyTags.Meal>.Value;

            if (useJobs)
            {
                SpawnMealsJobs(count, initialTags);
            }
            else
            {
                SpawnMealsMainThread(count, initialTags);
            }
        }

        void SpawnMealsMainThread(int count, TagSet initialTags)
        {
            for (int i = 0; i < count; i++)
            {
                World
                    .AddEntity(initialTags)
                    .Set(new UniformScale(_settings.MealSize))
                    .Set(
                        new Position(
                            FrenzyUtil.ChooseRandomMapPosition(
                                World.Rng.Next(),
                                World.Rng.Next(),
                                _settings.SpawnSpread,
                                _settings.SpawnConcentration,
                                _settings.MealYOffset
                            )
                        )
                    );
            }
        }

        void SpawnMealsJobs(int count, TagSet initialTags)
        {
            uint baseSeed = World.Rng.NextUint();

            var reservedRefs = World.ReserveEntityHandles(count, Allocator.TempJob);

            var jobHandle = new SpawnMealJob
            {
                ReservedRefs = reservedRefs,
                Tags = initialTags,
                BaseSeed = baseSeed,
                MealSize = _settings.MealSize,
                SpawnSpread = _settings.SpawnSpread,
                SpawnConcentration = _settings.SpawnConcentration,
                MealYOffset = _settings.MealYOffset,
            }.ScheduleParallel(World, count);

            reservedRefs.Dispose(jobHandle);
        }

        void DespawnMeals(int count, in FrenzyConfig config)
        {
            switch (config.SubsetApproach)
            {
                case FrenzySubsetApproach.Partitions:
                    DespawnMealsPartitions(count);
                    break;
                case FrenzySubsetApproach.Sets:
                    DespawnMealsSets(count);
                    break;
                default:
                    DespawnMealsDefault(count);
                    break;
            }
        }

        void DespawnMealsDefault(int count)
        {
            int removed = 0;

            foreach (var entityIndex in World.Query().WithTags<FrenzyTags.Meal>().EntityIndices())
            {
                if (removed >= count)
                {
                    return;
                }

                if (!World.Component<ApproachingFish>(entityIndex).Read.Value.IsNull)
                {
                    continue;
                }

                World.RemoveEntity(entityIndex);
                removed++;
            }

            foreach (var entityIndex in World.Query().WithTags<FrenzyTags.Meal>().EntityIndices())
            {
                if (removed >= count)
                {
                    return;
                }

                World.RemoveEntity(entityIndex);
                removed++;
            }
        }

        void DespawnMealsSets(int count)
        {
            int removed = 0;

            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Meal>()
                    .InSet<FrenzySets.NotEating>()
                    .EntityIndices()
            )
            {
                if (removed >= count)
                {
                    return;
                }

                World.RemoveEntity(entityIndex);
                removed++;
            }

            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Meal>()
                    .InSet<FrenzySets.Eating>()
                    .EntityIndices()
            )
            {
                if (removed >= count)
                {
                    return;
                }

                World.RemoveEntity(entityIndex);
                removed++;
            }
        }

        void DespawnMealsPartitions(int count)
        {
            int removed = 0;

            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Meal, FrenzyTags.NotEating>()
                    .EntityIndices()
            )
            {
                if (removed >= count)
                {
                    return;
                }

                World.RemoveEntity(entityIndex);
                removed++;
            }

            foreach (
                var entityIndex in World
                    .Query()
                    .WithTags<FrenzyTags.Meal, FrenzyTags.Eating>()
                    .EntityIndices()
            )
            {
                World.RemoveEntity(entityIndex);
                removed++;

                if (removed >= count)
                {
                    return;
                }
            }
        }

        [BurstCompile]
        partial struct SpawnMealJob : IJobFor
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ReadOnly]
            public NativeArray<EntityHandle> ReservedRefs;
            public TagSet Tags;
            public uint BaseSeed;
            public float MealSize;
            public float SpawnSpread;
            public float SpawnConcentration;
            public float MealYOffset;

            public void Execute(int i)
            {
                var rng = new Random(BaseSeed + (uint)i * 0x9E3779B9u + 1);
                var pos = FrenzyUtil.ChooseRandomMapPosition(
                    rng.NextFloat(),
                    rng.NextFloat(),
                    SpawnSpread,
                    SpawnConcentration,
                    MealYOffset
                );

                World
                    .AddEntity(Tags, (uint)i, ReservedRefs[i])
                    .Set(new UniformScale(MealSize))
                    .Set(new Position(pos));
            }
        }
    }
}
