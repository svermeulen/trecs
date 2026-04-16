using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Sets
{
    [ExecutesAfter(typeof(IConsumingMeal))]
    public partial class IdleBobSystem : IIdleBob, ISystem
    {
        static readonly TrecsLog _log = new(nameof(IdleBobSystem));

        const float GoldenRatio = 1.61803f;

        readonly IdleBobSystemSettings _settings;

        public IdleBobSystem(IdleBobSystemSettings settings)
        {
            _settings = settings;
        }

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
                    RunWrapAsJobAspect(_settings);
                    break;
                case IterationStyle.WrapAsJobComponents:
                    RunWrapAsJobComponents(_settings);
                    break;
                case IterationStyle.RawComponentBuffersJob:
                    RunRawComponentBuffersJob();
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
        void RunForEachMethodAspect(in Fish fish)
        {
            float baseY = _settings.BobBaseY * fish.UniformScale;
            float phaseOffset = fish.EntityIndex.Index * GoldenRatio;
            fish.Position.y =
                baseY
                + _settings.BobAmplitude
                    * fish.UniformScale
                    * math.sin(_settings.BobFrequency * World.ElapsedTime + phaseOffset);
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
        void RunForEachMethodComponents(
            in UniformScale uniformScale,
            ref Position position,
            EntityIndex entityIndex
        )
        {
            float baseY = _settings.BobBaseY * uniformScale.Value;
            float phaseOffset = entityIndex.Index * GoldenRatio;
            position.Value.y =
                baseY
                + _settings.BobAmplitude
                    * uniformScale.Value
                    * math.sin(_settings.BobFrequency * World.ElapsedTime + phaseOffset);
        }

        void RunAspectQuery()
        {
            foreach (
                var fish in Fish.Query(World)
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.NotEating>()
            )
            {
                float baseY = _settings.BobBaseY * fish.UniformScale;
                float phaseOffset = fish.EntityIndex.Index * GoldenRatio;
                fish.Position.y =
                    baseY
                    + _settings.BobAmplitude
                        * fish.UniformScale
                        * math.sin(_settings.BobFrequency * World.ElapsedTime + phaseOffset);
            }
        }

        void RunQueryGroupSlices()
        {
            foreach (
                var slice in World
                    .Query()
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.NotEating>()
                    .GroupSlices()
            )
            {
                var positions = World.ComponentBuffer<Position>(slice.Group).Write;
                var scales = World.ComponentBuffer<UniformScale>(slice.Group).Read;

                foreach (var i in slice.Indices)
                {
                    float baseY = _settings.BobBaseY * scales[i].Value;
                    float phaseOffset = i * GoldenRatio;
                    positions[i].Value.y =
                        baseY
                        + _settings.BobAmplitude
                            * scales[i].Value
                            * math.sin(_settings.BobFrequency * World.ElapsedTime + phaseOffset);
                }
            }
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(
            in Fish fish,
            in NativeWorldAccessor world,
            [PassThroughArgument] IdleBobSystemSettings settings
        )
        {
            float baseY = settings.BobBaseY * fish.UniformScale;
            float phaseOffset = fish.EntityIndex.Index * GoldenRatio;
            fish.Position.y =
                baseY
                + settings.BobAmplitude
                    * fish.UniformScale
                    * math.sin(settings.BobFrequency * world.ElapsedTime + phaseOffset);
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in UniformScale uniformScale,
            ref Position position,
            EntityIndex entityIndex,
            in NativeWorldAccessor world,
            [PassThroughArgument] IdleBobSystemSettings settings
        )
        {
            float baseY = settings.BobBaseY * uniformScale.Value;
            float phaseOffset = entityIndex.Index * GoldenRatio;
            position.Value.y =
                baseY
                + settings.BobAmplitude
                    * uniformScale.Value
                    * math.sin(settings.BobFrequency * world.ElapsedTime + phaseOffset);
        }

        void RunForEachMethodAspectJob()
        {
            new BobAspectJob
            {
                ElapsedTime = World.ElapsedTime,
                BobAmplitude = _settings.BobAmplitude,
                BobFrequency = _settings.BobFrequency,
                BobBaseY = _settings.BobBaseY,
            }.ScheduleParallel(World);
        }

        void RunForEachMethodComponentsJob()
        {
            new BobComponentsJob
            {
                ElapsedTime = World.ElapsedTime,
                BobAmplitude = _settings.BobAmplitude,
                BobFrequency = _settings.BobFrequency,
                BobBaseY = _settings.BobBaseY,
            }.ScheduleParallel(World);
        }

        void RunRawComponentBuffersJob()
        {
            var fishGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Fish>();
            var setRead = World.Set<FrenzySets.NotEating>().Read;

            if (!setRead.TryGetGroupEntry(fishGroup, out var entry) || entry.Count == 0)
            {
                return;
            }

            new BobRawBuffersJob
            {
                ElapsedTime = World.ElapsedTime,
                BobAmplitude = _settings.BobAmplitude,
                BobFrequency = _settings.BobFrequency,
                BobBaseY = _settings.BobBaseY,
            }.ScheduleParallel(World, count: entry.Count);
        }

        [BurstCompile]
        partial struct BobAspectJob
        {
            public float ElapsedTime;
            public float BobAmplitude;
            public float BobFrequency;
            public float BobBaseY;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
            public readonly void Execute(in Fish fish, EntityIndex entityIndex)
            {
                float baseY = BobBaseY * fish.UniformScale;
                float phaseOffset = entityIndex.Index * GoldenRatio;
                fish.Position.y =
                    baseY
                    + BobAmplitude
                        * fish.UniformScale
                        * math.sin(BobFrequency * ElapsedTime + phaseOffset);
            }
        }

        [BurstCompile]
        partial struct BobComponentsJob
        {
            public float ElapsedTime;
            public float BobAmplitude;
            public float BobFrequency;
            public float BobBaseY;

            [ForEachEntity(Tag = typeof(FrenzyTags.Fish), Set = typeof(FrenzySets.NotEating))]
            public readonly void Execute(
                in UniformScale uniformScale,
                ref Position position,
                EntityIndex entityIndex
            )
            {
                float baseY = BobBaseY * uniformScale.Value;
                float phaseOffset = entityIndex.Index * GoldenRatio;
                position.Value.y =
                    baseY
                    + BobAmplitude
                        * uniformScale.Value
                        * math.sin(BobFrequency * ElapsedTime + phaseOffset);
            }
        }

        [BurstCompile]
        partial struct BobRawBuffersJob
        {
            public float ElapsedTime;
            public float BobAmplitude;
            public float BobFrequency;
            public float BobBaseY;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeEntitySetIndices<FrenzySets.NotEating> FilterIndices;

            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<UniformScale> Scales;

            [NativeDisableParallelForRestriction]
            [FromWorld(Tag = typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<Position> Positions;

            public void Execute(int i)
            {
                int idx = FilterIndices[i];
                var pos = Positions[idx].Value;
                float baseY = BobBaseY * Scales[idx].Value;
                float phaseOffset = idx * GoldenRatio;
                pos.y =
                    baseY
                    + BobAmplitude
                        * Scales[idx].Value
                        * math.sin(BobFrequency * ElapsedTime + phaseOffset);
                Positions[idx].Value = pos;
            }
        }

        partial struct Fish : IAspect, IRead<UniformScale>, IWrite<Position> { }
    }
}
