using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Branching
{
    public partial class LookingForMealSystem : ILookingForMeal, ISystem
    {
        public void Execute()
        {
            ref readonly var config = ref World.GlobalComponent<FrenzyConfig>().Read;

            switch (config.IterationStyle)
            {
                case IterationStyle.ForEachMethodAspect:
                case IterationStyle.ForEachMethodComponents:
                case IterationStyle.AspectQuery:
                    RunAspectQuery();
                    break;
                case IterationStyle.QueryGroupSlices:
                    RunQueryGroupSlices();
                    break;
                case IterationStyle.WrapAsJobAspect:
                case IterationStyle.ForEachMethodAspectJob:
                case IterationStyle.WrapAsJobComponents:
                case IterationStyle.ForEachMethodComponentsJob:
                case IterationStyle.RawComponentBuffersJob:
                    RunRawComponentBuffersJob();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        void RunAspectQuery()
        {
            var mealIter = Meal.Query(World).WithTags<FrenzyTags.Meal>().GetEnumerator();

            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>())
            {
                if (!fish.TargetMeal.IsNull)
                {
                    continue;
                }

                bool foundMeal = false;

                while (mealIter.MoveNext())
                {
                    if (mealIter.Current.ApproachingFish.IsNull)
                    {
                        foundMeal = true;
                        break;
                    }
                }

                if (!foundMeal)
                {
                    break;
                }

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
        }

        void RunQueryGroupSlices()
        {
            var mealSliceIter = World.Query().WithTags<FrenzyTags.Meal>().GroupSlices();

            var mealPositions = default(NativeComponentBufferRead<Position>);
            var mealApproachingFish = default(NativeComponentBufferWrite<ApproachingFish>);
            var mealGroup = default(GroupIndex);
            int mi = 0;
            int mealSliceCount = 0;
            DenseGroupSlice mealSlice;

            foreach (var fishSlice in World.Query().WithTags<FrenzyTags.Fish>().GroupSlices())
            {
                var positions = World.ComponentBuffer<Position>(fishSlice.GroupIndex).Read;
                var speeds = World.ComponentBuffer<Speed>(fishSlice.GroupIndex).Read;
                var fishMeals = World.ComponentBuffer<TargetMeal>(fishSlice.GroupIndex).Write;
                var velocities = World.ComponentBuffer<Velocity>(fishSlice.GroupIndex).Write;
                var destPositions = World
                    .ComponentBuffer<DestinationPosition>(fishSlice.GroupIndex)
                    .Write;
                var rotations = World.ComponentBuffer<Rotation>(fishSlice.GroupIndex).Write;

                for (int fi = 0; fi < fishSlice.Count; fi++)
                {
                    if (!fishMeals[fi].Value.IsNull)
                    {
                        continue;
                    }

                    while (true)
                    {
                        while (mi >= mealSliceCount)
                        {
                            if (!mealSliceIter.MoveNext())
                            {
                                return;
                            }

                            mealSlice = mealSliceIter.Current;
                            mealPositions = World
                                .ComponentBuffer<Position>(mealSlice.GroupIndex)
                                .Read;
                            mealApproachingFish = World
                                .ComponentBuffer<ApproachingFish>(mealSlice.GroupIndex)
                                .Write;
                            mealGroup = mealSlice.GroupIndex;
                            mealSliceCount = mealSlice.Count;
                            mi = 0;
                        }

                        if (mealApproachingFish[mi].Value.IsNull)
                        {
                            break;
                        }

                        mi++;
                    }

                    var mealEntityIndex = new EntityIndex(mi, mealGroup);
                    var mealPos = mealPositions[mi].Value;

                    fishMeals[fi].Value = mealEntityIndex.ToHandle(World);

                    var destPos = mealPos;
                    destPos.y = positions[fi].Value.y;
                    destPositions[fi].Value = destPos;

                    var dir = math.normalize(destPos - positions[fi].Value);
                    rotations[fi].Value = quaternion.LookRotationSafe(dir, math.up());
                    velocities[fi].Value = dir * speeds[fi].Value;

                    mealApproachingFish[mi].Value = new EntityIndex(
                        fi,
                        fishSlice.GroupIndex
                    ).ToHandle(World);

                    mi++;
                }
            }
        }

        void AddFishMealPairs(NativeList<int2> pairs)
        {
            var fishGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Fish>();
            var mealGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Meal>();

            var fishCount = World.CountEntitiesInGroup(fishGroup);
            var mealCount = World.CountEntitiesInGroup(mealGroup);

            if (fishCount == 0 || mealCount == 0)
            {
                return;
            }

            pairs.Capacity = math.min(fishCount, mealCount);

            var fishMeals = World.ComponentBuffer<TargetMeal>(fishGroup).Read;
            var mealApproachingFish = World.ComponentBuffer<ApproachingFish>(mealGroup).Write;

            int mealIdx = 0;
            for (int fi = 0; fi < fishCount; fi++)
            {
                if (!fishMeals[fi].Value.IsNull)
                {
                    continue;
                }

                while (mealIdx < mealCount && !mealApproachingFish[mealIdx].Value.IsNull)
                {
                    mealIdx++;
                }

                if (mealIdx >= mealCount)
                {
                    break;
                }

                pairs.Add(new int2(fi, mealIdx));
                mealApproachingFish[mealIdx].Value = new EntityIndex(fi, fishGroup).ToHandle(World);
                mealIdx++;
            }
        }

        void RunRawComponentBuffersJob()
        {
            var pairs = new NativeList<int2>(Allocator.TempJob);

            // Unlike sets and states we need to prepare pairs on main thread
            AddFishMealPairs(pairs);

            if (pairs.Length == 0)
            {
                pairs.Dispose();
                return;
            }

            // Phase 2: parallel scatter via the source-gen pattern with raw buffers.
            var jobHandle = new RawBuffersScatterJob { Pairs = pairs.AsArray() }.ScheduleParallel(
                World,
                count: pairs.Length
            );

            pairs.Dispose(jobHandle);
        }

        [BurstCompile]
        partial struct RawBuffersScatterJob : IJobFor
        {
            [ReadOnly]
            public NativeArray<int2> Pairs;

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

            [FromWorld(typeof(FrenzyTags.Meal))]
            public GroupIndex MealGroup;

            [FromWorld]
            public NativeWorldAccessor World;

            public void Execute(int i)
            {
                var pair = Pairs[i];
                int fi = pair.x;
                int mi = pair.y;

                var fishPos = FishPositions[fi].Value;
                var mealPos = MealPositions[mi].Value;
                var speed = FishSpeeds[fi].Value;

                var destPos = mealPos;
                destPos.y = fishPos.y;
                var dir = math.normalize(destPos - fishPos);

                FishMeals[fi] = new TargetMeal
                {
                    Value = World.GetEntityHandle(new EntityIndex(mi, MealGroup)),
                };
                FishDestPositions[fi] = new DestinationPosition { Value = destPos };
                FishRotations[fi] = new Rotation
                {
                    Value = quaternion.LookRotationSafe(dir, math.up()),
                };
                FishVelocities[fi] = new Velocity { Value = dir * speed };
            }
        }

        partial struct Fish
            : IAspect,
                IRead<Position, Speed>,
                IWrite<TargetMeal, Velocity, DestinationPosition, Rotation> { }

        partial struct Meal : IAspect, IWrite<ApproachingFish>, IRead<Position> { }
    }
}
