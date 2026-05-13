using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Sets
{
    [ExecuteAfter(typeof(IConsumingMeal))]
    public partial class MovementSystem : IMovement, ISystem
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

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        void RunForEachMethodAspect(in Fish fish)
        {
            fish.Position += World.DeltaTime * fish.Velocity;
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        void RunForEachMethodComponents(in Velocity velocity, ref Position position)
        {
            position.Value += World.DeltaTime * velocity.Value;
        }

        void RunAspectQuery()
        {
            foreach (
                var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>().InSet<FrenzySets.Eating>()
            )
            {
                fish.Position += World.DeltaTime * fish.Velocity;
            }
        }

        void RunQueryGroupSlices()
        {
            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.Eating>()
                    .GroupSlices()
            )
            {
                var velocities = World.ComponentBuffer<Velocity>(slice.GroupIndex).Read;
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Write;

                foreach (var i in slice.Indices)
                {
                    positions[i].Value += World.DeltaTime * velocities[i].Value;
                }
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(in Fish fish, in NativeWorldAccessor world)
        {
            fish.Position += world.DeltaTime * fish.Velocity;
        }

        [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in Velocity velocity,
            ref Position position,
            in NativeWorldAccessor world
        )
        {
            position.Value += world.DeltaTime * velocity.Value;
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
            var setRead = World.Set<FrenzySets.Eating>().Read;

            if (!setRead.TryGetGroupEntry(fishGroup, out var entry) || entry.Count == 0)
            {
                return;
            }

            new MoveRawBuffersJob { DeltaTime = World.DeltaTime }.ScheduleParallel(
                World,
                count: entry.Count
            );
        }

        [BurstCompile]
        partial struct MoveAspectJob
        {
            public float DeltaTime;

            [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
            public readonly void Execute(in Fish fish)
            {
                fish.Position += DeltaTime * fish.Velocity;
            }
        }

        [BurstCompile]
        partial struct MoveComponentsJob
        {
            public float DeltaTime;

            [ForEachEntity(typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.Eating))]
            public readonly void Execute(in Velocity velocity, ref Position position)
            {
                position.Value += DeltaTime * velocity.Value;
            }
        }

        [BurstCompile]
        partial struct MoveRawBuffersJob
        {
            public float DeltaTime;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeEntitySetIndices<FrenzySets.Eating> FilterIndices;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<Velocity> Velocities;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<Position> Positions;

            public void Execute(int i)
            {
                int idx = FilterIndices[i];
                Positions[idx] = new Position
                {
                    Value = Positions[idx].Value + DeltaTime * Velocities[idx].Value,
                };
            }
        }

        partial struct Fish : IAspect, IRead<Velocity>, IWrite<Position> { }
    }
}
