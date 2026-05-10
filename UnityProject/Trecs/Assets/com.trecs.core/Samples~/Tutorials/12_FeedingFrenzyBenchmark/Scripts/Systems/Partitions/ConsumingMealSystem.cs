using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Partitions
{
    [ExecuteAfter(typeof(ILookingForMeal))]
    public partial class ConsumingMealSystem : IConsumingMeal, ISystem
    {
        static readonly TrecsLog _log = new(nameof(ConsumingMealSystem));

        public void Execute()
        {
            ref readonly var config = ref World.GlobalComponent<FrenzyConfig>().Read;

            switch (config.IterationStyle)
            {
                case IterationStyle.ForEachMethodAspect:
                    RunForEachMethodAspect();
                    break;
                case IterationStyle.ForEachMethodComponents:
                    RunForEachMethodComponents();
                    break;
                case IterationStyle.AspectQuery:
                    RunAspectQuery();
                    break;
                case IterationStyle.QueryGroupSlices:
                    RunQueryGroupSlices();
                    break;
                case IterationStyle.RawComponentBuffersJob:
                    RunRawComponentBuffersJob();
                    break;
                case IterationStyle.ForEachMethodAspectJob:
                    RunForEachMethodAspectJob();
                    break;
                case IterationStyle.ForEachMethodComponentsJob:
                    RunForEachMethodComponentsJob();
                    break;
                case IterationStyle.WrapAsJobAspect:
                    RunWrapAsJobAspect();
                    break;
                case IterationStyle.WrapAsJobComponents:
                    RunWrapAsJobComponents();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        void RunForEachMethodAspect(in Fish fish)
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                World.RemoveEntity(fish.TargetMeal);

                fish.TargetMeal = EntityHandle.Null;
                fish.SetTag<FrenzyTags.NotEating>(World);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        void RunForEachMethodComponents(
            in Position position,
            in DestinationPosition destinationPosition,
            ref TargetMeal fishMeal,
            EntityIndex entityIndex
        )
        {
            var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

            if (distanceSqr < EatDistanceSqr)
            {
                World.RemoveEntity(fishMeal.Value);

                fishMeal.Value = EntityHandle.Null;
                World.SetTag<FrenzyTags.NotEating>(entityIndex);
            }
        }

        void RunAspectQuery()
        {
            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish, FrenzyTags.Eating>())
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    World.RemoveEntity(fish.TargetMeal);

                    fish.TargetMeal = EntityHandle.Null;
                    fish.SetTag<FrenzyTags.NotEating>(World);
                }
            }
        }

        void RunQueryGroupSlices()
        {
            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish, FrenzyTags.Eating>()
                    .GroupSlices()
            )
            {
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Read;
                var destPositions = World
                    .ComponentBuffer<DestinationPosition>(slice.GroupIndex)
                    .Read;
                var meals = World.ComponentBuffer<TargetMeal>(slice.GroupIndex).Write;
                for (int i = 0; i < slice.Count; i++)
                {
                    var distanceSqr = math.lengthsq(destPositions[i].Value - positions[i].Value);
                    if (distanceSqr < EatDistanceSqr)
                    {
                        World.RemoveEntity(meals[i].Value.ToIndex(World));

                        meals[i].Value = EntityHandle.Null;
                        World.SetTag<FrenzyTags.NotEating>(
                            new EntityIndex(i, slice.GroupIndex)
                        );
                    }
                }
            }
        }

        void RunForEachMethodAspectJob()
        {
            new ConsumeMealAspectJob().ScheduleParallel(World);
        }

        void RunForEachMethodComponentsJob()
        {
            new ConsumeMealComponentsJob().ScheduleParallel(World);
        }

        void RunRawComponentBuffersJob()
        {
            // Parallel raw-buffer iteration over the eating-fish group only.
            // (Partitions approach uses tag-based partitions, so the eating fish are in
            // their own group separate from not-eating fish.)
            var fishCount = World.CountEntitiesWithTags<FrenzyTags.Fish, FrenzyTags.Eating>();

            if (fishCount == 0)
            {
                return;
            }

            new ConsumeMealRawBuffersJob().ScheduleParallel(World, count: fishCount);
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(
            in Fish fish,
            EntityIndex entityIndex,
            in NativeWorldAccessor world
        )
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                if (!world.TryGetEntityIndex(fish.TargetMeal, out var mealIndex))
                {
                    return;
                }

                world.RemoveEntity(mealIndex);
                fish.TargetMeal = EntityHandle.Null;
                world.SetTag<FrenzyTags.NotEating>(entityIndex);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in Position position,
            in DestinationPosition destinationPosition,
            ref TargetMeal fishMeal,
            EntityIndex entityIndex,
            in NativeWorldAccessor world
        )
        {
            var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

            if (distanceSqr < EatDistanceSqr)
            {
                if (!world.TryGetEntityIndex(fishMeal.Value, out var mealIndex))
                {
                    return;
                }

                world.RemoveEntity(mealIndex);
                fishMeal.Value = EntityHandle.Null;
                world.SetTag<FrenzyTags.NotEating>(entityIndex);
            }
        }

        [BurstCompile]
        partial struct ConsumeMealAspectJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public void Execute(in Fish fish, EntityIndex entityIndex)
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    if (!World.TryGetEntityIndex(fish.TargetMeal, out var mealIndex))
                    {
                        return;
                    }

                    World.RemoveEntity(mealIndex);
                    fish.TargetMeal = EntityHandle.Null;
                    World.SetTag<FrenzyTags.NotEating>(entityIndex);
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealComponentsJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public void Execute(
                in Position position,
                in DestinationPosition destinationPosition,
                ref TargetMeal fishMeal,
                EntityIndex entityIndex
            )
            {
                var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

                if (distanceSqr < EatDistanceSqr)
                {
                    if (!World.TryGetEntityIndex(fishMeal.Value, out var mealIndex))
                    {
                        return;
                    }

                    World.RemoveEntity(mealIndex);
                    fishMeal.Value = EntityHandle.Null;
                    World.SetTag<FrenzyTags.NotEating>(entityIndex);
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealRawBuffersJob
        {
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public GroupIndex FishGroup;

            [FromWorld]
            public NativeWorldAccessor World;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public NativeComponentBufferRead<Position> Positions;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public NativeComponentBufferRead<DestinationPosition> DestinationPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public NativeComponentBufferWrite<TargetMeal> Meals;

            public void Execute(int i)
            {
                var distanceSqr = math.lengthsq(DestinationPositions[i].Value - Positions[i].Value);

                if (distanceSqr < EatDistanceSqr)
                {
                    if (!World.TryGetEntityIndex(Meals[i].Value, out var mealIndex))
                    {
                        return;
                    }

                    World.RemoveEntity(mealIndex);
                    Meals[i] = new TargetMeal { Value = EntityHandle.Null };
                    World.SetTag<FrenzyTags.NotEating>(
                        new EntityIndex(i, FishGroup)
                    );
                }
            }
        }

        partial struct Fish : IAspect, IRead<Position, DestinationPosition>, IWrite<TargetMeal> { }

        const float EatDistance = 1f;
        const float EatDistanceSqr = EatDistance * EatDistance;
    }
}
