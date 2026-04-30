using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Branching
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

        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) })]
        void RunForEachMethodAspect(in Fish fish)
        {
            if (fish.TargetMeal.IsNull)
            {
                return;
            }

            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                World.RemoveEntity(fish.TargetMeal.ToIndex(World));

                fish.TargetMeal = EntityHandle.Null;
            }
        }

        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) })]
        void RunForEachMethodComponents(
            in Position position,
            in DestinationPosition destinationPosition,
            ref TargetMeal fishMeal,
            EntityIndex entityIndex
        )
        {
            if (fishMeal.Value.IsNull)
            {
                return;
            }

            var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

            if (distanceSqr < EatDistanceSqr)
            {
                World.RemoveEntity(fishMeal.Value.ToIndex(World));

                fishMeal.Value = EntityHandle.Null;
            }
        }

        void RunAspectQuery()
        {
            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>())
            {
                if (fish.TargetMeal.IsNull)
                {
                    continue;
                }

                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    World.RemoveEntity(fish.TargetMeal.ToIndex(World));

                    fish.TargetMeal = EntityHandle.Null;
                }
            }
        }

        void RunQueryGroupSlices()
        {
            foreach (var slice in World.Query().WithTags<FrenzyTags.Fish>().GroupSlices())
            {
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Read;
                var destPositions = World
                    .ComponentBuffer<DestinationPosition>(slice.GroupIndex)
                    .Read;
                var meals = World.ComponentBuffer<TargetMeal>(slice.GroupIndex).Write;

                for (int fi = 0; fi < slice.Count; fi++)
                {
                    if (meals[fi].Value.IsNull)
                    {
                        continue;
                    }

                    var distanceSqr = math.lengthsq(destPositions[fi].Value - positions[fi].Value);

                    if (distanceSqr < EatDistanceSqr)
                    {
                        World.RemoveEntity(meals[fi].Value.ToIndex(World));

                        meals[fi].Value = EntityHandle.Null;
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
            var fishCount = World.CountEntitiesWithTags<FrenzyTags.Fish>();

            if (fishCount == 0)
            {
                return;
            }

            new ConsumeMealRawBuffersJob().ScheduleParallel(World, count: fishCount);
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(in Fish fish, in NativeWorldAccessor world)
        {
            if (fish.TargetMeal.IsNull)
            {
                return;
            }

            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                var mealIndex = fish.TargetMeal.ToIndex(world);
                world.RemoveEntity(mealIndex);
                fish.TargetMeal = EntityHandle.Null;
            }
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in Position position,
            in DestinationPosition destinationPosition,
            ref TargetMeal fishMeal,
            in NativeWorldAccessor world
        )
        {
            if (fishMeal.Value.IsNull)
            {
                return;
            }

            var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

            if (distanceSqr < EatDistanceSqr)
            {
                var mealIndex = fishMeal.Value.ToIndex(world);
                world.RemoveEntity(mealIndex);
                fishMeal.Value = EntityHandle.Null;
            }
        }

        [BurstCompile]
        partial struct ConsumeMealAspectJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
            public void Execute(in Fish fish)
            {
                if (fish.TargetMeal.IsNull)
                {
                    return;
                }

                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    var mealIndex = fish.TargetMeal.ToIndex(World);
                    World.RemoveEntity(mealIndex);
                    fish.TargetMeal = EntityHandle.Null;
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealComponentsJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
            public void Execute(
                in Position position,
                in DestinationPosition destinationPosition,
                ref TargetMeal fishMeal,
                EntityIndex entityIndex
            )
            {
                if (fishMeal.Value.IsNull)
                {
                    return;
                }

                var distanceSqr = math.lengthsq(destinationPosition.Value - position.Value);

                if (distanceSqr < EatDistanceSqr)
                {
                    var mealIndex = fishMeal.Value.ToIndex(World);
                    World.RemoveEntity(mealIndex);
                    fishMeal.Value = EntityHandle.Null;
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealRawBuffersJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Position> Positions;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<DestinationPosition> DestinationPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<TargetMeal> Meals;

            public void Execute(int i)
            {
                if (Meals[i].Value.IsNull)
                {
                    return;
                }

                var distanceSqr = math.lengthsq(DestinationPositions[i].Value - Positions[i].Value);

                if (distanceSqr < EatDistanceSqr)
                {
                    var mealIndex = Meals[i].Value.ToIndex(World);
                    World.RemoveEntity(mealIndex);
                    Meals[i].Value = EntityHandle.Null;
                }
            }
        }

        partial struct Fish : IAspect, IRead<Position, DestinationPosition>, IWrite<TargetMeal> { }

        const float EatDistance = 1f;
        const float EatDistanceSqr = EatDistance * EatDistance;
    }
}
