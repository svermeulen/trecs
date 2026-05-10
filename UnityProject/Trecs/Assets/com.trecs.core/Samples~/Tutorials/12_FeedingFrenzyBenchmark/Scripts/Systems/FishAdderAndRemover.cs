using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [Serializable]
    public class FishAdderAndRemoverSettings
    {
        public float FishYOffset = -2f;
        public float AddSpeed = 1f;
        public float2 FishSpeedRange = new(15f, 4f);
        public float2 FishSizeRange = new(1f, 2f);
    }

    public partial class FishAdderAndRemover : IManageFishCount, ISystem
    {
        readonly CommonSettings _commonSettings;
        readonly int[] _presets;
        readonly FishAdderAndRemoverSettings _settings;

        public FishAdderAndRemover(
            FishAdderAndRemoverSettings settings,
            CommonSettings commonSettings,
            int[] presets
        )
        {
            _settings = settings;
            _commonSettings = commonSettings;
            _presets = presets;
        }

        void Execute([SingleEntity(typeof(TrecsTags.Globals))] in GlobalsView globals)
        {
            int presetIndex = globals.DesiredPreset;

            Assert.That(presetIndex >= 0);

            int desiredFishCount = _presets[math.clamp(presetIndex, 0, _presets.Length - 1)];
            globals.DesiredFishCount = (int)
                math.lerp(
                    globals.DesiredFishCount,
                    desiredFishCount,
                    math.saturate(_settings.AddSpeed * World.DeltaTime)
                );

            int desiredMealCount = (int)(globals.DesiredFishCount * _commonSettings.MealCountRatio);
            globals.DesiredMealCount = desiredMealCount;

            int currentCount = World.CountEntitiesWithTags<FrenzyTags.Fish>();
            int delta = globals.DesiredFishCount - currentCount;

            if (delta > 0)
            {
                SpawnFish(delta, globals);
            }
            else if (delta < 0)
            {
                RemoveFish(-delta, globals);
            }
        }

        void SpawnFish(int count, in GlobalsView globals)
        {
            bool isJobs =
                globals.FrenzyConfig.IterationStyle
                is IterationStyle.ForEachMethodAspectJob
                    or IterationStyle.ForEachMethodComponentsJob
                    or IterationStyle.WrapAsJobAspect
                    or IterationStyle.WrapAsJobComponents
                    or IterationStyle.RawComponentBuffersJob;
            var tags =
                globals.FrenzyConfig.SubsetApproach is FrenzySubsetApproach.Partitions
                    ? TagSet<FrenzyTags.Fish, FrenzyTags.NotEating>.Value
                    : TagSet<FrenzyTags.Fish>.Value;

            if (isJobs)
            {
                SpawnFishJobs(count, tags);
            }
            else
            {
                SpawnFishMainThread(count, tags);
            }
        }

        void SpawnFishMainThread(int count, TagSet tags)
        {
            for (int i = 0; i < count; i++)
            {
                var scalePx = World.Rng.Next();
                var scale = math.lerp(
                    _settings.FishSizeRange.x,
                    _settings.FishSizeRange.y,
                    scalePx
                );

                var pos = FrenzyUtil.ChooseRandomMapPosition(
                    World.Rng.Next(),
                    World.Rng.Next(),
                    _commonSettings.SpawnSpread,
                    _commonSettings.SpawnConcentration,
                    y: _settings.FishYOffset * scale
                );

                World
                    .AddEntity(tags)
                    .Set(
                        new Speed(
                            math.lerp(
                                _settings.FishSpeedRange.x,
                                _settings.FishSpeedRange.y,
                                scalePx
                            )
                        )
                    )
                    .Set(new Position(pos))
                    .Set(new UniformScale(scale));
            }
        }

        void SpawnFishJobs(int count, TagSet tags)
        {
            uint baseSeed = World.Rng.NextUint();
            var nativeWorld = World.ToNative();
            var reservedRefs = World.ReserveEntityHandles(count, Allocator.TempJob);

            var jobHandle = new SpawnFishJob
            {
                World = nativeWorld,
                ReservedRefs = reservedRefs,
                Tags = tags,
                BaseSeed = baseSeed,
                FishSizeMin = _settings.FishSizeRange.x,
                FishSizeMax = _settings.FishSizeRange.y,
                FishSpeedMin = _settings.FishSpeedRange.x,
                FishSpeedMax = _settings.FishSpeedRange.y,
                FishYOffset = _settings.FishYOffset,
                SpawnSpread = _commonSettings.SpawnSpread,
                SpawnConcentration = _commonSettings.SpawnConcentration,
            }.ScheduleParallel(World, count);

            reservedRefs.Dispose(jobHandle);
        }

        void RemoveFish(int count, in GlobalsView globals)
        {
            switch (globals.FrenzyConfig.SubsetApproach)
            {
                case FrenzySubsetApproach.Partitions:
                    RemoveFishPartitions(count);
                    break;

                case FrenzySubsetApproach.Sets:
                    RemoveFishSets(count);
                    break;

                case FrenzySubsetApproach.Branching:
                    RemoveFishBranching(count);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        void RemoveFishSets(int count)
        {
            Assert.That(count > 0);

            int removed = 0;

            foreach (
                var entity in World
                    .Query()
                    .WithTags<FrenzyTags.Fish>()
                    .InSet<FrenzySets.NotEating>()
                    .Entities()
            )
            {
                entity.Remove();
                removed++;

                if (removed >= count)
                {
                    break;
                }
            }

            foreach (
                var fish in Fish.Query(World).WithTags<FrenzyTags.Fish>().InSet<FrenzySets.Eating>()
            )
            {
                fish.Remove(World);
                World.RemoveEntity(fish.TargetMeal);

                removed++;

                if (removed >= count)
                {
                    break;
                }
            }
        }

        void RemoveFishBranching(int count)
        {
            Assert.That(count > 0);

            int removed = 0;

            foreach (var fish in BranchingFish.Query(World).WithTags<FrenzyTags.Fish>())
            {
                if (!fish.TargetMeal.IsNull)
                {
                    continue;
                }

                fish.Remove(World);
                removed++;

                if (removed >= count)
                {
                    break;
                }
            }

            foreach (var fish in BranchingFish.Query(World).WithTags<FrenzyTags.Fish>())
            {
                if (fish.TargetMeal.IsNull)
                {
                    continue;
                }

                fish.Remove(World);
                World.RemoveEntity(fish.TargetMeal);

                removed++;

                if (removed >= count)
                {
                    break;
                }
            }
        }

        void RemoveFishPartitions(int count)
        {
            Assert.That(count > 0);

            int removed = 0;

            foreach (
                var entity in World
                    .Query()
                    .WithTags<FrenzyTags.Fish, FrenzyTags.NotEating>()
                    .Entities()
            )
            {
                entity.Remove();
                removed++;

                if (removed >= count)
                {
                    break;
                }
            }

            foreach (var fish in Fish.Query(World).WithTags<FrenzyTags.Fish, FrenzyTags.Eating>())
            {
                fish.Remove(World);
                removed++;

                if (removed >= count)
                {
                    break;
                }
            }
        }

        [BurstCompile]
        partial struct SpawnFishJob : IJobFor
        {
            [FromWorld]
            public NativeWorldAccessor World;

            [ReadOnly]
            public NativeArray<EntityHandle> ReservedRefs;

            public TagSet Tags;
            public uint BaseSeed;
            public float FishSizeMin;
            public float FishSizeMax;
            public float FishSpeedMin;
            public float FishSpeedMax;
            public float FishYOffset;
            public float SpawnSpread;
            public float SpawnConcentration;

            public void Execute(int i)
            {
                var rng = new Random(BaseSeed + (uint)i * 0x9E3779B9u + 1);

                var scalePx = rng.NextFloat();
                var scale = math.lerp(FishSizeMin, FishSizeMax, scalePx);

                var pos = FrenzyUtil.ChooseRandomMapPosition(
                    rng.NextFloat(),
                    rng.NextFloat(),
                    SpawnSpread,
                    SpawnConcentration,
                    FishYOffset * scale
                );

                World
                    .AddEntity(Tags, (uint)i, ReservedRefs[i])
                    .Set(new Speed(math.lerp(FishSpeedMin, FishSpeedMax, scalePx)))
                    .Set(new Position(pos))
                    .Set(new UniformScale(scale));
            }
        }

        partial struct BranchingFish : IAspect, IRead<TargetMeal> { }

        partial struct Fish : IAspect, IRead<TargetMeal> { }

        partial struct GlobalsView
            : IAspect,
                IRead<DesiredPreset, FrenzyConfig>,
                IWrite<DesiredFishCount, DesiredMealCount> { }
    }
}
