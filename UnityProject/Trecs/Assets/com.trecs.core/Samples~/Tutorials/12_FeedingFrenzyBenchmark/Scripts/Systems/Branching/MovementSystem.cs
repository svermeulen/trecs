using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Branching
{
    [ExecuteAfter(typeof(IConsumingMeal))]
    public partial class MovementSystem : IMovement, ISystem
    {
        static readonly TrecsLog _log = new(nameof(MovementSystem));

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
                case IterationStyle.RawComponentBuffersJob:
                    RunRawComponentBuffersJob();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) })]
        void RunForEachMethodAspect(in Fish fish)
        {
            if (!fish.TargetMeal.IsNull)
            {
                fish.Position += World.DeltaTime * fish.Velocity;
            }
        }

        [ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish) })]
        void RunForEachMethodComponents(
            in Velocity velocity,
            in TargetMeal meal,
            ref Position position
        )
        {
            if (!meal.Value.IsNull)
            {
                position.Value += World.DeltaTime * velocity.Value;
            }
        }

        void RunAspectQuery()
        {
            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>())
            {
                if (!fish.TargetMeal.IsNull)
                {
                    fish.Position += World.DeltaTime * fish.Velocity;
                }
            }
        }

        void RunQueryGroupSlices()
        {
            foreach (var slice in World.Query().WithTags<FrenzyTags.Fish>().GroupSlices())
            {
                var velocities = World.ComponentBuffer<Velocity>(slice.GroupIndex).Read;
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Write;
                var targetMeals = World.ComponentBuffer<TargetMeal>(slice.GroupIndex).Read;

                for (int fi = 0; fi < slice.Count; fi++)
                {
                    if (!targetMeals[fi].Value.IsNull)
                    {
                        positions[fi].Value += World.DeltaTime * velocities[fi].Value;
                    }
                }
            }
        }

        void RunForEachMethodAspectJob()
        {
            new MoveAspectJob { DeltaTime = World.DeltaTime }.ScheduleParallel(World);
        }

        void RunForEachMethodComponentsJob()
        {
            new MoveComponentsJob { DeltaTime = World.DeltaTime }.ScheduleParallel(World);
        }

        void RunRawComponentBuffersJob()
        {
            var fishGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Fish>();
            var fishCount = World.CountEntitiesInGroup(fishGroup);

            if (fishCount == 0)
            {
                return;
            }

            new MoveRawBuffersJob { DeltaTime = World.DeltaTime }.ScheduleParallel(
                World,
                count: fishCount
            );
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(in Fish fish, in NativeWorldAccessor world)
        {
            if (!fish.TargetMeal.IsNull)
            {
                fish.Position += world.DeltaTime * fish.Velocity;
            }
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in Velocity velocity,
            in TargetMeal meal,
            ref Position position,
            in NativeWorldAccessor world
        )
        {
            if (!meal.Value.IsNull)
            {
                position.Value += world.DeltaTime * velocity.Value;
            }
        }

        [BurstCompile]
        partial struct MoveAspectJob
        {
            public float DeltaTime;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
            public readonly void Execute(in Fish fish)
            {
                if (!fish.TargetMeal.IsNull)
                {
                    fish.Position += DeltaTime * fish.Velocity;
                }
            }
        }

        [BurstCompile]
        partial struct MoveComponentsJob
        {
            public float DeltaTime;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
            public readonly void Execute(
                in Velocity velocity,
                in TargetMeal meal,
                ref Position position
            )
            {
                if (!meal.Value.IsNull)
                {
                    position.Value += DeltaTime * velocity.Value;
                }
            }
        }

        [BurstCompile]
        partial struct MoveRawBuffersJob
        {
            public float DeltaTime;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Velocity> Velocities;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<TargetMeal> TargetMeals;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<Position> Positions;

            public void Execute(int i)
            {
                if (!TargetMeals[i].Value.IsNull)
                {
                    Positions[i] = new Position
                    {
                        Value = Positions[i].Value + DeltaTime * Velocities[i].Value,
                    };
                }
            }
        }

        partial struct Fish : IAspect, IRead<Velocity, TargetMeal>, IWrite<Position> { }
    }
}
