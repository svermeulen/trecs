using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Sets
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

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        void RunForEachMethodAspect(in Fish fish, EntityHandle entityHandle)
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                fish.TargetMeal.Remove(World);
                fish.TargetMeal = EntityHandle.Null;

                World.Set<FrenzySets.Eating>().DeferredRemove(entityHandle);
                World.Set<FrenzySets.NotEating>().DeferredAdd(entityHandle);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
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

                World.Set<FrenzySets.Eating>().DeferredRemove(entityHandle);
                World.Set<FrenzySets.NotEating>().DeferredAdd(entityHandle);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(in Fish fish, in NativeWorldAccessor world)
        {
            var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

            if (distanceSqr < EatDistanceSqr)
            {
                fish.TargetMeal.Remove(world);
                fish.TargetMeal = EntityHandle.Null;
                world.Set<FrenzySets.Eating>().DeferredRemove(fish.EntityIndex);
                world.Set<FrenzySets.NotEating>().DeferredAdd(fish.EntityIndex);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
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
                fishMeal.Value.Remove(world);
                fishMeal.Value = EntityHandle.Null;
                world.Set<FrenzySets.Eating>().DeferredRemove(entityHandle);
                world.Set<FrenzySets.NotEating>().DeferredAdd(entityHandle);
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
                    var fishHandle = fish.Handle(World);
                    fish.TargetMeal.Remove(World);
                    fish.TargetMeal = EntityHandle.Null;

                    World.Set<FrenzySets.Eating>().DeferredRemove(fishHandle);
                    World.Set<FrenzySets.NotEating>().DeferredAdd(fishHandle);
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
                        meals[idx].Value.Remove(World);
                        meals[idx].Value = EntityHandle.Null;
                        toMoveToNotEating.Add(new EntityIndex(idx, slice.GroupIndex));
                    }
                }
            }

            foreach (var entityIndex in toMoveToNotEating)
            {
                eatingSet.Write.Remove(entityIndex);
                notEatingSet.Write.Add(entityIndex);
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

            [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
            public void Execute(in Fish fish)
            {
                var distanceSqr = math.lengthsq(fish.DestinationPosition - fish.Position);

                if (distanceSqr < EatDistanceSqr)
                {
                    fish.TargetMeal.Remove(World);
                    fish.TargetMeal = EntityHandle.Null;
                    World.Set<FrenzySets.Eating>().DeferredRemove(fish.EntityIndex);
                    World.Set<FrenzySets.NotEating>().DeferredAdd(fish.EntityIndex);
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealComponentsJob
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
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
                    fishMeal.Value.Remove(World);
                    fishMeal.Value = EntityHandle.Null;
                    World.Set<FrenzySets.Eating>().DeferredRemove(entityHandle);
                    World.Set<FrenzySets.NotEating>().DeferredAdd(entityHandle);
                }
            }
        }

        [BurstCompile]
        partial struct ConsumeMealRawBuffersJob
        {
            [FromWorld(typeof(FrenzyTags.Fish))]
            public GroupIndex FishGroup;

            [FromWorld]
            public NativeWorldAccessor World;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeEntitySetIndices<FrenzySets.Eating> FilterIndices;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Position> Positions;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<DestinationPosition> DestinationPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<TargetMeal> Meals;

            public void Execute(int i)
            {
                int idx = FilterIndices[i];
                var distanceSqr = math.lengthsq(
                    DestinationPositions[idx].Value - Positions[idx].Value
                );

                if (distanceSqr < EatDistanceSqr)
                {
                    Meals[idx].Value.Remove(World);
                    Meals[idx] = new TargetMeal { Value = EntityHandle.Null };

                    var fishEntityIndex = new EntityIndex(idx, FishGroup);
                    World.Set<FrenzySets.Eating>().DeferredRemove(fishEntityIndex);
                    World.Set<FrenzySets.NotEating>().DeferredAdd(fishEntityIndex);
                }
            }
        }

        partial struct Fish : IAspect, IRead<Position, DestinationPosition>, IWrite<TargetMeal> { }

        const float EatDistance = 1f;
        const float EatDistanceSqr = EatDistance * EatDistance;
    }
}
