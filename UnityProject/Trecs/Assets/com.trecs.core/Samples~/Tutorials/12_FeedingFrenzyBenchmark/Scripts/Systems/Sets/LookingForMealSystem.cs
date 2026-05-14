using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Sets
{
    public partial class LookingForMealSystem : ILookingForMeal, ISystem
    {
        public void Execute()
        {
            ref readonly var config = ref World.GlobalComponent<FrenzyConfig>().Read;

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

        void RunAspectQuery()
        {
            var mealIter = Meal.Query(World)
                .WithTags<FrenzyTags.Meal>()
                .InSet<FrenzySets.NotEating>()
                .GetEnumerator();

            foreach (
                var fish in Fish.Query(World)
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.NotEating>()
            )
            {
                if (!mealIter.MoveNext())
                {
                    break;
                }

                var meal = mealIter.Current;

                var fishHandle = fish.Handle(World);
                var mealHandle = meal.Handle(World);

                fish.TargetMeal = mealHandle;
                fish.DestinationPosition = meal.Position;
                fish.DestinationPosition.y = fish.Position.y;

                var destinationDir = math.normalize(fish.DestinationPosition - fish.Position);

                fish.Rotation = quaternion.LookRotationSafe(destinationDir, math.up());
                fish.Velocity = destinationDir * fish.Speed;

                meal.ApproachingFish = fishHandle;

                World.Set<FrenzySets.NotEating>().Defer.Remove(fishHandle);
                World.Set<FrenzySets.Eating>().Defer.Add(fishHandle);

                World.Set<FrenzySets.NotEating>().Defer.Remove(mealHandle);
                World.Set<FrenzySets.Eating>().Defer.Add(mealHandle);
            }
        }

        void RunQueryGroupSlices()
        {
            var mealSliceIter = World
                .Query()
                .WithTags<FrenzyTags.Meal>()
                .InSet<FrenzySets.NotEating>()
                .GroupSlices()
                .GetEnumerator();

            var mealPositions = default(NativeComponentBufferRead<Position>);
            var mealApproachingFish = default(NativeComponentBufferWrite<ApproachingFish>);
            var mealGroup = default(GroupIndex);
            int mealIndexIdx = 0;
            int mealIndicesCount = 0;

            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.NotEating>()
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

                foreach (var fi in slice.Indices)
                {
                    var fishEntityIndex = new EntityIndex(fi, slice.GroupIndex);

                    while (mealIndexIdx >= mealIndicesCount)
                    {
                        if (!mealSliceIter.MoveNext())
                        {
                            return;
                        }

                        var mealSlice = mealSliceIter.Current;
                        mealPositions = World.ComponentBuffer<Position>(mealSlice.GroupIndex).Read;
                        mealApproachingFish = World
                            .ComponentBuffer<ApproachingFish>(mealSlice.GroupIndex)
                            .Write;
                        mealGroup = mealSlice.GroupIndex;
                        mealIndicesCount = mealSlice.Indices.Count;
                        mealIndexIdx = 0;
                    }

                    var mealIdx = mealSliceIter.Current.Indices[mealIndexIdx];
                    mealIndexIdx++;

                    var mealEntityIndex = new EntityIndex(mealIdx, mealGroup);
                    var mealPos = mealPositions[mealIdx].Value;

                    meals[fi].Value = mealEntityIndex.ToHandle(World);
                    mealApproachingFish[mealIdx].Value = fishEntityIndex.ToHandle(World);

                    var destPos = mealPos;
                    destPos.y = positions[fi].Value.y;
                    destPositions[fi].Value = destPos;

                    var dir = math.normalize(destPos - positions[fi].Value);
                    rotations[fi].Value = quaternion.LookRotationSafe(dir, math.up());
                    velocities[fi].Value = dir * speeds[fi].Value;

                    World.Set<FrenzySets.NotEating>().Defer.Remove(fishEntityIndex);
                    World.Set<FrenzySets.Eating>().Defer.Add(fishEntityIndex);

                    World.Set<FrenzySets.NotEating>().Defer.Remove(mealEntityIndex);
                    World.Set<FrenzySets.Eating>().Defer.Add(mealEntityIndex);
                }
            }
        }

        void RunRawComponentBuffersJob()
        {
            var fishGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Fish>();
            var mealGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Meal>();
            var setRead = World.Set<FrenzySets.NotEating>().Read;

            int fishCount = setRead.TryGetGroupEntry(fishGroup, out var fishEntry)
                ? fishEntry.Count
                : 0;
            int mealCount = setRead.TryGetGroupEntry(mealGroup, out var mealEntry)
                ? mealEntry.Count
                : 0;
            int pairCount = math.min(fishCount, mealCount);

            if (pairCount == 0)
                return;

            new RawBuffersScatterJob().ScheduleParallel(World, count: pairCount);
        }

        [BurstCompile]
        partial struct RawBuffersScatterJob : IJobFor
        {
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeEntitySetIndices<FrenzySets.NotEating> FishFilterIndices;

            [FromWorld(typeof(FrenzyTags.Meal))]
            public NativeEntitySetIndices<FrenzySets.NotEating> MealFilterIndices;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public GroupIndex FishGroup;

            [FromWorld(typeof(FrenzyTags.Meal))]
            public GroupIndex MealGroup;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Position> FishPositions;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Speed> FishSpeeds;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<TargetMeal> FishMeals;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<Velocity> FishVelocities;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<DestinationPosition> FishDestPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<Rotation> FishRotations;

            [FromWorld(typeof(FrenzyTags.Meal))]
            public NativeComponentBufferRead<Position> MealPositions;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Meal))]
            public NativeComponentBufferWrite<ApproachingFish> MealApproachingFish;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeEntityHandleBuffer FishEntityHandles;

            [FromWorld(typeof(FrenzyTags.Meal))]
            public NativeEntityHandleBuffer MealEntityHandles;

            [FromWorld]
            public NativeWorldAccessor World;

            public void Execute(int i)
            {
                int fi = FishFilterIndices[i];
                int mi = MealFilterIndices[i];

                var fishPos = FishPositions[fi].Value;
                var mealPos = MealPositions[mi].Value;
                var speed = FishSpeeds[fi].Value;

                var destPos = mealPos;
                destPos.y = fishPos.y;
                var dir = math.normalize(destPos - fishPos);

                FishMeals[fi] = new TargetMeal { Value = MealEntityHandles[mi] };
                FishDestPositions[fi] = new DestinationPosition { Value = destPos };
                FishRotations[fi] = new Rotation
                {
                    Value = quaternion.LookRotationSafe(dir, math.up()),
                };
                FishVelocities[fi] = new Velocity { Value = dir * speed };

                MealApproachingFish[mi] = new ApproachingFish { Value = FishEntityHandles[fi] };

                var fishEntityIndex = new EntityIndex(fi, FishGroup);
                var mealEntityIndex = new EntityIndex(mi, MealGroup);
                World.SetRemove<FrenzySets.NotEating>(fishEntityIndex);
                World.SetAdd<FrenzySets.Eating>(fishEntityIndex);
                World.SetRemove<FrenzySets.NotEating>(mealEntityIndex);
                World.SetAdd<FrenzySets.Eating>(mealEntityIndex);
            }
        }

        partial struct Fish
            : IAspect,
                IRead<Position, Speed>,
                IWrite<TargetMeal, Velocity, DestinationPosition, Rotation> { }

        partial struct Meal : IAspect, IWrite<ApproachingFish>, IRead<Position> { }
    }
}
