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
            fish.TargetMeal = meal.EntityIndex.ToHandle(World);
            fish.DestinationPosition = meal.Position;
            fish.DestinationPosition.y = fish.Position.y;

            var destinationDir = math.normalize(fish.DestinationPosition - fish.Position);

            fish.Rotation = quaternion.LookRotationSafe(destinationDir, math.up());
            fish.Velocity = destinationDir * fish.Speed;

            meal.ApproachingFish = fish.EntityIndex.ToHandle(World);

            World.MoveTo<FrenzyTags.Fish, FrenzyTags.Eating>(fish.EntityIndex);
            World.MoveTo<FrenzyTags.Meal, FrenzyTags.Eating>(meal.EntityIndex);
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
            var mealGroup = default(Group);
            int mi = 0;
            int mealGroupCount = 0;

            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish, FrenzyTags.NotEating>()
                    .GroupSlices()
            )
            {
                var positions = World.ComponentBuffer<Position>(slice.Group).Read;
                var speeds = World.ComponentBuffer<Speed>(slice.Group).Read;
                var meals = World.ComponentBuffer<TargetMeal>(slice.Group).Write;
                var velocities = World.ComponentBuffer<Velocity>(slice.Group).Write;
                var destPositions = World.ComponentBuffer<DestinationPosition>(slice.Group).Write;
                var rotations = World.ComponentBuffer<Rotation>(slice.Group).Write;

                for (int fi = 0; fi < slice.Count; fi++)
                {
                    while (mi >= mealGroupCount)
                    {
                        if (!mealSliceIter.MoveNext())
                            return;

                        var mealSlice = mealSliceIter.Current;
                        mealPositions = World.ComponentBuffer<Position>(mealSlice.Group).Read;
                        mealApproachingFish = World
                            .ComponentBuffer<ApproachingFish>(mealSlice.Group)
                            .Write;
                        mealGroup = mealSlice.Group;
                        mealGroupCount = mealSlice.Count;
                        mi = 0;
                    }

                    var mealEntityIndex = new EntityIndex(mi, mealGroup);
                    var mealPos = mealPositions[mi].Value;

                    var fishEntityIndex = new EntityIndex(fi, slice.Group);

                    meals[fi].Value = mealEntityIndex.ToHandle(World);
                    mealApproachingFish[mi].Value = fishEntityIndex.ToHandle(World);

                    var destPos = mealPos;
                    destPos.y = positions[fi].Value.y;
                    destPositions[fi].Value = destPos;

                    var dir = math.normalize(destPos - positions[fi].Value);
                    rotations[fi].Value = quaternion.LookRotationSafe(dir, math.up());
                    velocities[fi].Value = dir * speeds[fi].Value;

                    World.MoveTo<FrenzyTags.Fish, FrenzyTags.Eating>(fishEntityIndex);
                    World.MoveTo<FrenzyTags.Meal, FrenzyTags.Eating>(mealEntityIndex);

                    mi++;
                }
            }
        }

        [BurstCompile]
        partial struct RawComponentBuffersJob : IJobFor
        {
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferRead<Position> MealPositions;

            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferRead<Position> FishPositions;

            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferRead<Speed> FishSpeeds;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferWrite<TargetMeal> Meals;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferWrite<Velocity> Velocities;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferWrite<DestinationPosition> DestinationPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferWrite<Rotation> Rotations;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating) })]
            public NativeComponentBufferWrite<ApproachingFish> MealApproachingFish;

            // We could also call World.GetEntityHandle instead but by injecting the NativeEntityHandleBuffer
            // we save a dictionary lookup per entity
            [FromWorld(Tags = new[] { typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating) })]
            public NativeEntityHandleBuffer MealEntityHandles;

            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public NativeEntityHandleBuffer FishEntityHandles;

            [FromWorld]
            public NativeWorldAccessor World;

            [FromWorld(Tags = new[] { typeof(FrenzyTags.Meal), typeof(FrenzyTags.NotEating) })]
            public Group MealGroup;

            [FromWorld(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
            public Group FishGroup;

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
                World.MoveTo<FrenzyTags.Fish, FrenzyTags.Eating>(fishEntityIndex);
                World.MoveTo<FrenzyTags.Meal, FrenzyTags.Eating>(mealEntityIndex);
            }
        }

        partial struct Fish
            : IAspect,
                IRead<Position, Speed>,
                IWrite<TargetMeal, Velocity, DestinationPosition, Rotation> { }

        partial struct Meal : IAspect, IWrite<ApproachingFish>, IRead<Position> { }
    }
}
