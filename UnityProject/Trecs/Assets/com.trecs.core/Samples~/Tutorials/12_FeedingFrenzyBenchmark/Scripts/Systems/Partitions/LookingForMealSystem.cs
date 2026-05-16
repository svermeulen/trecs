using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// Unlike Branching/Filters, the Partitions approach uses tag-based groups to
// ensure NotEating fish and NotEating meals are in the same group with matching
// counts. This enables 1:1 index pairing (i-th fish pairs with i-th meal),
// making the operation fully parallelizable via jobs.

namespace Trecs.Samples.FeedingFrenzyBenchmark.Partitions
{
    public partial class LookingForMealSystem : ILookingForMeal, ISystem
    {
        public void Execute()
        {
            ref readonly var config = ref World.GlobalComponent<FrenzyConfig>().Read;

            // Since the logic here requires a double iteration over both fish and meals,
            // we can't implement all the different iteration styles, so we just choose
            // from a few approaches
            switch (config.IterationStyle)
            {
                case IterationStyle.QueryGroupSlices:
                    RunQueryGroupSlices();
                    break;

                case IterationStyle.ForEachMethodComponents:
                case IterationStyle.ForEachMethodAspect:
                case IterationStyle.AspectQuery:
                    RunAspectQuery();
                    break;

                case IterationStyle.WrapAsJobComponents:
                case IterationStyle.WrapAsJobAspect:
                case IterationStyle.ForEachMethodComponentsJob:
                case IterationStyle.ForEachMethodAspectJob:
                case IterationStyle.RawComponentBuffersJob:
                    RunRawComponentBuffersJob();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        void RunRawComponentBuffersJob()
        {
            // In this approach we take advantage of the fact that MealNotEating and FishNotEating
            // map to single groups
            // This allows us just to directly iterate over the component buffers for those groups

            var mealCount = World.CountEntitiesWithTags<FrenzyTags.Meal, FrenzyTags.NotEating>();
            var fishCount = World.CountEntitiesWithTags<FrenzyTags.Fish, FrenzyTags.NotEating>();

            var amountEatable = (int)math.min(fishCount, mealCount);

            if (amountEatable == 0)
            {
                return;
            }

            new RawComponentBuffersJob().ScheduleParallel(World, amountEatable);
        }

        void RunAspectQuery()
        {
            var mealIter = Meal.Query(World)
                .WithTags<FrenzyTags.Meal, FrenzyTags.NotEating>()
                .GetEnumerator();

            foreach (
                var fish in Fish.Query(World).WithTags<FrenzyTags.Fish, FrenzyTags.NotEating>()
            )
            {
                if (!mealIter.MoveNext())
                    break;

                PairFishWithMeal(fish, mealIter.Current);
            }
        }

        void PairFishWithMeal(in Fish fish, in Meal meal)
        {
            fish.TargetMeal = meal.Handle(World);
            fish.DestinationPosition = meal.Position;
            fish.DestinationPosition.y = fish.Position.y;

            var destinationDir = math.normalize(fish.DestinationPosition - fish.Position);

            fish.Rotation = quaternion.LookRotationSafe(destinationDir, math.up());
            fish.Velocity = destinationDir * fish.Speed;

            meal.ApproachingFish = fish.Handle(World);

            fish.SetTag<FrenzyTags.Eating>(World);
            meal.SetTag<FrenzyTags.Eating>(World);
        }

        void RunQueryGroupSlices()
        {
            var mealSliceIter = World
                .Query()
                .WithTags<FrenzyTags.Meal, FrenzyTags.NotEating>()
                .GroupSlices()
                .GetEnumerator();

            var mealPositions = default(NativeComponentBufferRead<Position>);
            var mealApproachingFish = default(NativeComponentBufferWrite<ApproachingFish>);
            var mealGroup = default(GroupIndex);
            int mi = 0;
            int mealGroupCount = 0;

            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish, FrenzyTags.NotEating>()
                    .GroupSlices()
            )
            {
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Read;
                var speeds = World.ComponentBuffer<Speed>(slice.GroupIndex).Read;
                var meals = World.ComponentBuffer<TargetMeal>(slice.GroupIndex).Write;
                var velocities = World.ComponentBuffer<Velocity>(slice.GroupIndex).Write;
                var destPositions = World
                    .ComponentBuffer<DestinationPosition>(slice.GroupIndex)
                    .Write;
                var rotations = World.ComponentBuffer<Rotation>(slice.GroupIndex).Write;

                for (int fi = 0; fi < slice.Count; fi++)
                {
                    while (mi >= mealGroupCount)
                    {
                        if (!mealSliceIter.MoveNext())
                            return;

                        var mealSlice = mealSliceIter.Current;
                        mealPositions = World.ComponentBuffer<Position>(mealSlice.GroupIndex).Read;
                        mealApproachingFish = World
                            .ComponentBuffer<ApproachingFish>(mealSlice.GroupIndex)
                            .Write;
                        mealGroup = mealSlice.GroupIndex;
                        mealGroupCount = mealSlice.Count;
                        mi = 0;
                    }

                    var mealEntityIndex = new EntityIndex(mi, mealGroup);
                    var mealPos = mealPositions[mi].Value;

                    var fishEntityIndex = new EntityIndex(fi, slice.GroupIndex);

                    meals[fi].Value = mealEntityIndex.ToHandle(World);
                    mealApproachingFish[mi].Value = fishEntityIndex.ToHandle(World);

                    var destPos = mealPos;
                    destPos.y = positions[fi].Value.y;
                    destPositions[fi].Value = destPos;

                    var dir = math.normalize(destPos - positions[fi].Value);
                    rotations[fi].Value = quaternion.LookRotationSafe(dir, math.up());
                    velocities[fi].Value = dir * speeds[fi].Value;

                    fishEntityIndex.SetTag<FrenzyTags.Eating>(World);
                    mealEntityIndex.SetTag<FrenzyTags.Eating>(World);

                    mi++;
                }
            }
        }

        [BurstCompile]
        partial struct RawComponentBuffersJob : IJobFor
        {
            [FromWorld(typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferRead<Position> MealPositions;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferRead<Position> FishPositions;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferRead<Speed> FishSpeeds;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferWrite<TargetMeal> Meals;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferWrite<Velocity> Velocities;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferWrite<DestinationPosition> DestinationPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferWrite<Rotation> Rotations;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating))]
            public NativeComponentBufferWrite<ApproachingFish> MealApproachingFish;

            // We could also call World.GetEntityHandle instead but by injecting the NativeEntityHandleBuffer
            // we save a dictionary lookup per entity
            [FromWorld(typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating))]
            public NativeEntityHandleBuffer MealEntityHandles;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public NativeEntityHandleBuffer FishEntityHandles;

            [FromWorld]
            public NativeWorldAccessor World;

            [FromWorld(typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating))]
            public GroupIndex MealGroup;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
            public GroupIndex FishGroup;

            public readonly void Execute(int i)
            {
                ref readonly var mealPos = ref MealPositions[i];

                ref var meal = ref Meals[i].Value;
                meal = MealEntityHandles[i];

                MealApproachingFish[i] = new ApproachingFish { Value = FishEntityHandles[i] };

                var destPos = mealPos.Value;
                destPos.y = FishPositions[i].Value.y;
                DestinationPositions[i].Value = destPos;

                var dir = math.normalize(destPos - FishPositions[i].Value);
                Rotations[i].Value = quaternion.LookRotationSafe(dir, math.up());
                Velocities[i].Value = dir * FishSpeeds[i].Value;

                var mealEntityIndex = new EntityIndex(i, MealGroup);
                var fishEntityIndex = new EntityIndex(i, FishGroup);
                fishEntityIndex.SetTag<FrenzyTags.Eating>(World);
                mealEntityIndex.SetTag<FrenzyTags.Eating>(World);
            }
        }

        partial struct Fish
            : IAspect,
                IRead<Position, Speed>,
                IWrite<TargetMeal, Velocity, DestinationPosition, Rotation> { }

        partial struct Meal : IAspect, IWrite<ApproachingFish>, IRead<Position> { }
    }
}
