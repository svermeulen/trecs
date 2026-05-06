using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Partitions
{
    [ExecuteAfter(typeof(IConsumingMeal))]
    public partial class MovementSystem : IMovement, ISystem
    {
        static readonly TrecsLog _log = new(nameof(MovementSystem));

        partial struct Fish : IAspect, IRead<Velocity>, IWrite<Position> { }

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
            fish.Position += World.DeltaTime * fish.Velocity;
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        void RunForEachMethodComponents(in Velocity velocity, ref Position position)
        {
            position.Value += World.DeltaTime * velocity.Value;
        }

        void RunAspectQuery()
        {
            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish, FrenzyTags.Eating>())
            {
                fish.Position += World.DeltaTime * fish.Velocity;
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
                var velocities = World.ComponentBuffer<Velocity>(slice.GroupIndex).Read;
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Write;
                for (int i = 0; i < slice.Count; i++)
                {
                    positions[i].Value += World.DeltaTime * velocities[i].Value;
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
            var eatingGroup = World.WorldInfo.GetSingleGroupWithTags<
                FrenzyTags.Fish,
                FrenzyTags.Eating
            >();
            var eatingCount = World.CountEntitiesInGroup(eatingGroup);
            if (eatingCount == 0)
                return;

            new MoveRawBuffersJob { DeltaTime = World.DeltaTime }.ScheduleParallel(
                World,
                count: eatingCount
            );
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(in Fish fish, in NativeWorldAccessor world)
        {
            fish.Position += world.DeltaTime * fish.Velocity;
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in Velocity velocity,
            ref Position position,
            in NativeWorldAccessor world
        )
        {
            position.Value += world.DeltaTime * velocity.Value;
        }

        [BurstCompile]
        partial struct MoveAspectJob
        {
            public float DeltaTime;

            [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public readonly void Execute(in Fish fish)
            {
                fish.Position += DeltaTime * fish.Velocity;
            }
        }

        [BurstCompile]
        partial struct MoveComponentsJob
        {
            public float DeltaTime;

            [ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public readonly void Execute(in Velocity velocity, ref Position position)
            {
                position.Value += DeltaTime * velocity.Value;
            }
        }

        [BurstCompile]
        partial struct MoveRawBuffersJob
        {
            public float DeltaTime;

            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public NativeComponentBufferRead<Velocity> Velocities;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
            public NativeComponentBufferWrite<Position> Positions;

            public void Execute(int i)
            {
                Positions[i] = new Position
                {
                    Value = Positions[i].Value + DeltaTime * Velocities[i].Value,
                };
            }
        }
    }
}
