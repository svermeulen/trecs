using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Sets
{
    [ExecutesAfter(typeof(ILookingForMeal))]
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

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        void RunForEachMethodAspect(in Fish fish, EntityIndex entityIndex)
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                World.RemoveEntity(fish.TargetMeal.ToIndex(World));
                fish.TargetMeal = EntityHandle.Null;

                World.SetRemove<FrenzySets.Eating>(entityIndex);
                World.SetAdd<FrenzySets.NotEating>(entityIndex);
            }
        }

        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) }, Set = typeof(FrenzySets.Eating))]
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
                World.RemoveEntity(fishMeal.Value.ToIndex(World));
                fishMeal.Value = EntityHandle.Null;

                World.SetRemove<FrenzySets.Eating>(entityIndex);
                World.SetAdd<FrenzySets.NotEating>(entityIndex);
            }
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(in Fish fish, in NativeWorldAccessor world)
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                var mealIndex = fish.TargetMeal.ToIndex(world);

                world.RemoveEntity(mealIndex);
                fish.TargetMeal = EntityHandle.Null;
                world.SetRemove<FrenzySets.Eating>(fish.EntityIndex);
                world.SetAdd<FrenzySets.NotEating>(fish.EntityIndex);
            }
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
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
                var mealIndex = fishMeal.Value.ToIndex(world);

                world.RemoveEntity(mealIndex);
                fishMeal.Value = EntityHandle.Null;
                world.SetRemove<FrenzySets.Eating>(entityIndex);
                world.SetAdd<FrenzySets.NotEating>(entityIndex);
            }
        }

        void RunAspectQuery()
        {
            foreach (
                var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>().InSet<FrenzySets.Eating>()
            )
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    World.RemoveEntity(fish.TargetMeal.ToIndex(World));
                    fish.TargetMeal = EntityHandle.Null;

                    World.SetRemove<FrenzySets.Eating>(fish.EntityIndex);
                    World.SetAdd<FrenzySets.NotEating>(fish.EntityIndex);
                }
            }
        }

        void RunQueryGroupSlices()
        {
            var eatingSet = World.Set<FrenzySets.Eating>();
            var notEatingSet = World.Set<FrenzySets.NotEating>();
            using var toMoveToNotEating = new NativeList<EntityIndex>(Allocator.Temp);

            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.Eating>()
                    .GroupSlices()
            )
            {
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Read;
                var destPositions = World
                    .ComponentBuffer<DestinationPosition>(slice.GroupIndex)
                    .Read;
                var meals = World.ComponentBuffer<TargetMeal>(slice.GroupIndex).Write;
                foreach (var idx in slice.Indices)
                {
                    var distanceSqr = math.lengthsq(
                        destPositions[idx].Value - positions[idx].Value
                    );
                    if (distanceSqr < EatDistanceSqr)
                    {
                        World.RemoveEntity(meals[idx].Value.ToIndex(World));
                        meals[idx].Value = EntityHandle.Null;
                        toMoveToNotEating.Add(new EntityIndex(idx, slice.GroupIndex));
                    }
                }
            }

            foreach (var entityIndex in toMoveToNotEating)
            {
                eatingSet.Write.RemoveImmediate(entityIndex);
                notEatingSet.Write.AddImmediate(entityIndex);
            }
        }

        void RunForEachMethodAspectJob()
        {
            new ForEachMethodAspectJob().ScheduleParallel(World);
        }

        void RunForEachMethodComponentsJob()
        {
            new ConsumeMealComponentsJob().ScheduleParallel(World);
        }

        void RunRawComponentBuffersJob()
        {
            var fishGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Fish>();
            var setRead = World.Set<FrenzySets.Eating>().Read;

            if (!setRead.TryGetGroupEntry(fishGroup, out var entry) || entry.Count == 0)
            {
                return;
            }

            new ConsumeMealRawBuffersJob().ScheduleParallel(World, count: entry.Count);
        }

        [BurstCompile]
        partial struct ForEachMethodAspectJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
            public void Execute(in Fish fish)
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    var mealIndex = fish.TargetMeal.ToIndex(World);

                    World.RemoveEntity(mealIndex);
                    fish.TargetMeal = EntityHandle.Null;
                    World.SetRemove<FrenzySets.Eating>(fish.EntityIndex);
                    World.SetAdd<FrenzySets.NotEating>(fish.EntityIndex);
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealComponentsJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
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
                    var mealIndex = fishMeal.Value.ToIndex(World);

                    World.RemoveEntity(mealIndex);
                    fishMeal.Value = EntityHandle.Null;
                    World.SetRemove<FrenzySets.Eating>(entityIndex);
                    World.SetAdd<FrenzySets.NotEating>(entityIndex);
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealRawBuffersJob
        {
            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public GroupIndex FishGroup;

            [FromWorld]
            public NativeWorldAccessor World;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeEntitySetIndices<FrenzySets.Eating> FilterIndices;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Position> Positions;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<DestinationPosition> DestinationPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<TargetMeal> Meals;

            public void Execute(int i)
            {
                int idx = FilterIndices[i];
                var distanceSqr = math.lengthsq(
                    DestinationPositions[idx].Value - Positions[idx].Value
                );

                if (distanceSqr < EatDistanceSqr)
                {
                    var mealIndex = Meals[idx].Value.ToIndex(World);

                    World.RemoveEntity(mealIndex);
                    Meals[idx] = new TargetMeal { Value = EntityHandle.Null };

                    var fishEntityIndex = new EntityIndex(idx, FishGroup);
                    World.SetRemove<FrenzySets.Eating>(fishEntityIndex);
                    World.SetAdd<FrenzySets.NotEating>(fishEntityIndex);
                }
            }
        }

        partial struct Fish : IAspect, IRead<Position, DestinationPosition>, IWrite<TargetMeal> { }

        const float EatDistance = 1f;
        const float EatDistanceSqr = EatDistance * EatDistance;
    }
}
