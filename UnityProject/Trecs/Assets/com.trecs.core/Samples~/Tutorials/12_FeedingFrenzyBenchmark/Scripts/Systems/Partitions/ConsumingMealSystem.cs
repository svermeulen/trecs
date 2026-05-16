using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Partitions
{
    [ExecuteAfter(typeof(ILookingForMeal))]
    public partial class ConsumingMealSystem : IConsumingMeal, ISystem
    {
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
                fish.TargetMeal.Remove(World);

                fish.TargetMeal = EntityHandle.Null;
                fish.SetTag<FrenzyTags.NotEating>(World);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        void RunForEachMethodComponents(
            in Position position,
            in DestinationPosition destinationPosition,
            ref TargetMeal fishMeal,
            EntityHandle entityHandle
        )
        {
            var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

            if (distanceSqr < EatDistanceSqr)
            {
                fishMeal.Value.Remove(World);

                fishMeal.Value = EntityHandle.Null;
                entityHandle.SetTag<FrenzyTags.NotEating>(World);
            }
        }

        void RunAspectQuery()
        {
            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish, FrenzyTags.Eating>())
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    fish.TargetMeal.Remove(World);

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
                        meals[i].Value.Remove(World);

                        meals[i].Value = EntityHandle.Null;
                        new EntityIndex(i, slice.GroupIndex).SetTag<FrenzyTags.NotEating>(World);
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
            EntityHandle entityHandle,
            in NativeWorldAccessor world
        )
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                if (!fish.TargetMeal.TryToIndex(world, out var mealIndex))
                {
                    return;
                }

                mealIndex.Remove(world);
                fish.TargetMeal = EntityHandle.Null;
                entityHandle.SetTag<FrenzyTags.NotEating>(world);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in Position position,
            in DestinationPosition destinationPosition,
            ref TargetMeal fishMeal,
            EntityHandle entityHandle,
            in NativeWorldAccessor world
        )
        {
            var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

            if (distanceSqr < EatDistanceSqr)
            {
                if (!fishMeal.Value.TryToIndex(world, out var mealIndex))
                {
                    return;
                }

                mealIndex.Remove(world);
                fishMeal.Value = EntityHandle.Null;
                entityHandle.SetTag<FrenzyTags.NotEating>(world);
            }
        }

        [BurstCompile]
        partial struct ConsumeMealAspectJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public void Execute(in Fish fish, EntityHandle entityHandle)
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    if (!fish.TargetMeal.TryToIndex(World, out var mealIndex))
                    {
                        return;
                    }

                    mealIndex.Remove(World);
                    fish.TargetMeal = EntityHandle.Null;
                    entityHandle.SetTag<FrenzyTags.NotEating>(World);
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
                EntityHandle entityHandle
            )
            {
                var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

                if (distanceSqr < EatDistanceSqr)
                {
                    if (!fishMeal.Value.TryToIndex(World, out var mealIndex))
                    {
                        return;
                    }

                    mealIndex.Remove(World);
                    fishMeal.Value = EntityHandle.Null;
                    entityHandle.SetTag<FrenzyTags.NotEating>(World);
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
                    if (!Meals[i].Value.TryToIndex(World, out var mealIndex))
                    {
                        return;
                    }

                    mealIndex.Remove(World);
                    Meals[i] = new TargetMeal { Value = EntityHandle.Null };
                    new EntityIndex(i, FishGroup).SetTag<FrenzyTags.NotEating>(World);
                }
            }
        }

        partial struct Fish : IAspect, IRead<Position, DestinationPosition>, IWrite<TargetMeal> { }

        const float EatDistance = 1f;
        const float EatDistanceSqr = EatDistance * EatDistance;
    }
}
