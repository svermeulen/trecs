using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark.Branching
{
    [ExecuteAfter(typeof(IConsumingMeal))]
    public partial class IdleBobSystem : IIdleBob, ISystem
    {
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
                    RunWrapAsJobAspect(_settings);
                    break;
                case IterationStyle.WrapAsJobComponents:
                    RunWrapAsJobComponents(_settings);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish))]
        void RunForEachMethodAspect(in Fish fish)
        {
            if (fish.TargetMeal.IsNull)
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

        [ForEachEntity(typeof(FrenzyTags.Fish))]
        void RunForEachMethodComponents(
            in TargetMeal targetMeal,
            in UniformScale uniformScale,
            ref Position position,
            EntityIndex entityIndex
        )
        {
            if (targetMeal.Value.IsNull)
            {
                float baseY = _settings.BobBaseY * uniformScale.Value;
                float phaseOffset = entityIndex.Index * GoldenRatio;
                position.Value.y =
                    baseY
                    + _settings.BobAmplitude
                        * uniformScale.Value
                        * math.sin(_settings.BobFrequency * World.ElapsedTime + phaseOffset);
            }
        }

        void RunAspectQuery()
        {
            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>())
            {
                if (fish.TargetMeal.IsNull)
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
        }

        void RunQueryGroupSlices()
        {
            foreach (var slice in World.Query().WithTags<FrenzyTags.Fish>().GroupSlices())
            {
                var positions = World.ComponentBuffer<Position>(slice.GroupIndex).Write;
                var targetMeals = World.ComponentBuffer<TargetMeal>(slice.GroupIndex).Read;
                var scales = World.ComponentBuffer<UniformScale>(slice.GroupIndex).Read;

                for (int fi = 0; fi < slice.Count; fi++)
                {
                    if (targetMeals[fi].Value.IsNull)
                    {
                        float baseY = _settings.BobBaseY * scales[fi].Value;
                        float phaseOffset = fi * GoldenRatio;
                        positions[fi].Value.y =
                            baseY
                            + _settings.BobAmplitude
                                * scales[fi].Value
                                * math.sin(
                                    _settings.BobFrequency * World.ElapsedTime + phaseOffset
                                );
                    }
                }
            }
        }

        void RunForEachMethodAspectJob()
        {
            new BobAspectJob
            {
                ElapsedTime = World.ElapsedTime,
                Settings = _settings,
            }.ScheduleParallel(World);
        }

        [ForEachEntity(typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void RunWrapAsJobAspect(
            in Fish fish,
            in NativeWorldAccessor world,
            [PassThroughArgument] IdleBobSystemSettings settings
        )
        {
            if (fish.TargetMeal.IsNull)
            {
                float baseY = settings.BobBaseY * fish.UniformScale;
                float phaseOffset = fish.EntityIndex.Index * GoldenRatio;
                fish.Position.y =
                    baseY
                    + settings.BobAmplitude
                        * fish.UniformScale
                        * math.sin(settings.BobFrequency * world.ElapsedTime + phaseOffset);
            }
        }

        [ForEachEntity(typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void RunWrapAsJobComponents(
            in TargetMeal targetMeal,
            in UniformScale uniformScale,
            ref Position position,
            EntityIndex entityIndex,
            in NativeWorldAccessor world,
            [PassThroughArgument] IdleBobSystemSettings settings
        )
        {
            if (targetMeal.Value.IsNull)
            {
                float baseY = settings.BobBaseY * uniformScale.Value;
                float phaseOffset = entityIndex.Index * GoldenRatio;
                position.Value.y =
                    baseY
                    + settings.BobAmplitude
                        * uniformScale.Value
                        * math.sin(settings.BobFrequency * world.ElapsedTime + phaseOffset);
            }
        }

        void RunForEachMethodComponentsJob()
        {
            new BobComponentsJob
            {
                ElapsedTime = World.ElapsedTime,
                Settings = _settings,
            }.ScheduleParallel(World);
        }

        void RunRawComponentBuffersJob()
        {
            var fishGroup = World.WorldInfo.GetSingleGroupWithTags<FrenzyTags.Fish>();
            var fishCount = World.CountEntitiesInGroup(fishGroup);

            if (fishCount == 0)
            {
                return;
            }

            new BobRawBuffersJob
            {
                ElapsedTime = World.ElapsedTime,
                Settings = _settings,
            }.ScheduleParallel(World, count: fishCount);
        }

        [BurstCompile]
        partial struct BobAspectJob
        {
            public float ElapsedTime;
            public IdleBobSystemSettings Settings;

            [ForEachEntity(typeof(FrenzyTags.Fish))]
            public readonly void Execute(in Fish fish, EntityIndex entityIndex)
            {
                if (fish.TargetMeal.IsNull)
                {
                    float baseY = Settings.BobBaseY * fish.UniformScale;
                    float phaseOffset = entityIndex.Index * GoldenRatio;
                    fish.Position.y =
                        baseY
                        + Settings.BobAmplitude
                            * fish.UniformScale
                            * math.sin(Settings.BobFrequency * ElapsedTime + phaseOffset);
                }
            }
        }

        [BurstCompile]
        partial struct BobComponentsJob
        {
            public float ElapsedTime;
            public IdleBobSystemSettings Settings;

            [ForEachEntity(typeof(FrenzyTags.Fish))]
            public readonly void Execute(
                in TargetMeal targetMeal,
                in UniformScale uniformScale,
                ref Position position,
                EntityIndex entityIndex
            )
            {
                if (targetMeal.Value.IsNull)
                {
                    float baseY = Settings.BobBaseY * uniformScale.Value;
                    float phaseOffset = entityIndex.Index * GoldenRatio;
                    position.Value.y =
                        baseY
                        + Settings.BobAmplitude
                            * uniformScale.Value
                            * math.sin(Settings.BobFrequency * ElapsedTime + phaseOffset);
                }
            }
        }

        [BurstCompile]
        partial struct BobRawBuffersJob
        {
            public float ElapsedTime;
            public IdleBobSystemSettings Settings;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<TargetMeal> TargetMeals;

            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferRead<UniformScale> Scales;

            [NativeDisableParallelForRestriction]
            [FromWorld(typeof(FrenzyTags.Fish))]
            public NativeComponentBufferWrite<Position> Positions;

            public void Execute(int i)
            {
                if (TargetMeals[i].Value.IsNull)
                {
                    float baseY = Settings.BobBaseY * Scales[i].Value;
                    float phaseOffset = i * GoldenRatio;
                    Positions[i].Value.y =
                        baseY
                        + Settings.BobAmplitude
                            * Scales[i].Value
                            * math.sin(Settings.BobFrequency * ElapsedTime + phaseOffset);
                }
            }
        }

        partial struct Fish : IAspect, IRead<TargetMeal, UniformScale>, IWrite<Position> { }
    }
}
