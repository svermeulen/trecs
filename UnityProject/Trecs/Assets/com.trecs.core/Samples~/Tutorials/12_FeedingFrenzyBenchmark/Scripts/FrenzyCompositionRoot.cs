// Companion docs: https://svermeulen.github.io/trecs/samples/12-feeding-frenzy-benchmark/

using System;
using System.Collections.Generic;
using TMPro;
using Trecs;
using Trecs.Collections;
using Trecs.Internal;
using Trecs.Samples.FeedingFrenzyBenchmark.Branching;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public class FrenzyCompositionRoot : CompositionRootBase
    {
        static readonly TrecsLog _log = new(nameof(FrenzyCompositionRoot));

        public FrenzyConfigSettings Config;
        public SampleSettings Settings;
        public TMP_Text DisplayText;
        public GameObject ProfilerRunner;

        public static FrenzyConfigSettings ConfigOverride;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // Allow static override for F1/F2/F3 maps
            FrenzyConfigSettings config;

            if (ConfigOverride == null)
            {
                config = Config;
            }
            else
            {
                config = ConfigOverride;
            }

            var fishCountPresets = ComputePresetFishCounts(
                Settings.Common.MinPresetFishCount,
                Settings.Common.MaxPresetFishCount
            );
            int maxPresetFishCount = fishCountPresets[^1];
            int maxPresetMealCount = (int)(maxPresetFishCount * Settings.Common.MealCountRatio);

            var worldBuilder = new WorldBuilder()
                .SetSettings(
                    new WorldSettings
                    {
                        RandomSeed = config.Deterministic ? 42 : (ulong)DateTime.Now.Ticks,
                        RequireDeterministicSubmission = config.Deterministic,
                    }
                )
                .AddEntityTypes(GetTemplates(config));

            if (config.SubsetApproach == FrenzySubsetApproach.Sets)
            {
                worldBuilder.AddSet<FrenzySets.Eating>().AddSet<FrenzySets.NotEating>();
            }

            var world = worldBuilder.Build();

            var subsetApproachDynamicSwitcher = new SubsetApproachDynamicSwitcher(world);

            var fishSetInitializer = new InitialSetsApplier(config, world);
            var removeCleanupHandler = new RemoveCleanupHandler(world);
            var configInput = new ConfigInputSystem();
            var presetInput = new FishCountPresetInputSystem(fishCountPresets);
            var manageFishCount = new FishAdderAndRemover(
                Settings.FishAdderAndRemover,
                Settings.Common,
                fishCountPresets
            );

            var manageMealCount = new MealAdderAndRemover(Settings.Common);
            var lookingForMeal = CreateLookingForMealSystem(config);
            var consumingMeal = CreateConsumingMealSystem(config);
            var movement = CreateMovementSystem(config);
            var idleBob = CreateIdleBobSystem(config, Settings.IdleBob);
            var perfStats = new PerformanceStatsUpdater(world);
            var perfStatsDisplay = new TextDisplaySystem(
                DisplayText,
                fishCountPresets,
                Settings.Common
            );

            var sceneInitializer = new SceneInitializer(Settings.Common, world, config);

            var renderer = new RendererSystem();

            var fishMesh = SampleUtil.CreateDartMesh();
            var mealMesh = SampleUtil.CreateScaledCubeMesh();
            var fishMaterial = SampleUtil.CreateUnlitInstancedMaterial(Settings.Common.FishColor);
            var mealMaterial = SampleUtil.CreateUnlitInstancedMaterial(Settings.Common.MealColor);

            renderer.RegisterRenderable(
                TagSet<FrenzyTags.Fish>.Value,
                fishMesh,
                fishMaterial,
                maxPresetFishCount
            );

            renderer.RegisterRenderable(
                TagSet<FrenzyTags.Meal>.Value,
                mealMesh,
                mealMaterial,
                maxPresetMealCount
            );

            world.AddSystem(renderer);

            // NOTE: This list determines OnReady call order
            world.AddSystems(
                new ISystem[]
                {
                    configInput,
                    presetInput,
                    manageFishCount,
                    manageMealCount,
                    lookingForMeal,
                    consumingMeal,
                    movement,
                    idleBob,
                    perfStatsDisplay,
                }
            );

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new()
            {
                configInput.Tick,
                presetInput.Tick,
                world.Tick,
                subsetApproachDynamicSwitcher.Tick,
            };

            lateTickables = new() { world.LateTick };

            disposables = new()
            {
                fishSetInitializer.Dispose,
                removeCleanupHandler.Dispose,
                perfStats.Dispose,
                world.Dispose,
                renderer.Dispose,
            };
        }

        public static int[] ComputePresetFishCounts(int min, int max)
        {
            const int presetCount = 12;
            var fishCountPresets = new int[presetCount];
            float logMin = math.log2(math.max(1, min));
            float logMax = math.log2(math.max(1, max));

            for (int i = 0; i < presetCount; i++)
            {
                float t = i / (float)(presetCount - 1);
                fishCountPresets[i] = (int)math.round(math.pow(2f, math.lerp(logMin, logMax, t)));
            }

            return fishCountPresets;
        }

        Template[] GetTemplates(FrenzyConfigSettings config)
        {
            if (config.SubsetApproach == FrenzySubsetApproach.Partitions)
            {
                return new Template[]
                {
                    Templates.PartitionsFishEntity.Template,
                    Templates.PartitionsMealEntity.Template,
                    Templates.Globals.Template,
                };
            }

            return new Template[]
            {
                Templates.FishEntity.Template,
                Templates.MealEntity.Template,
                Templates.Globals.Template,
            };
        }

        static ISystem CreateLookingForMealSystem(FrenzyConfigSettings config) =>
            config.SubsetApproach switch
            {
                FrenzySubsetApproach.Branching => new LookingForMealSystem(),
                FrenzySubsetApproach.Sets => new Sets.LookingForMealSystem(),
                FrenzySubsetApproach.Partitions => new Partitions.LookingForMealSystem(),
                _ => throw new ArgumentOutOfRangeException(),
            };

        static ISystem CreateConsumingMealSystem(FrenzyConfigSettings config) =>
            config.SubsetApproach switch
            {
                FrenzySubsetApproach.Branching => new ConsumingMealSystem(),
                FrenzySubsetApproach.Sets => new Sets.ConsumingMealSystem(),
                FrenzySubsetApproach.Partitions => new Partitions.ConsumingMealSystem(),
                _ => throw new ArgumentOutOfRangeException(),
            };

        static ISystem CreateMovementSystem(FrenzyConfigSettings config) =>
            config.SubsetApproach switch
            {
                FrenzySubsetApproach.Branching => new MovementSystem(),
                FrenzySubsetApproach.Sets => new Sets.MovementSystem(),
                FrenzySubsetApproach.Partitions => new Partitions.MovementSystem(),
                _ => throw new ArgumentOutOfRangeException(),
            };

        static ISystem CreateIdleBobSystem(
            FrenzyConfigSettings config,
            IdleBobSystemSettings settings
        ) =>
            config.SubsetApproach switch
            {
                FrenzySubsetApproach.Branching => new IdleBobSystem(settings),
                FrenzySubsetApproach.Sets => new Sets.IdleBobSystem(settings),
                FrenzySubsetApproach.Partitions => new Partitions.IdleBobSystem(settings),
                _ => throw new ArgumentOutOfRangeException(),
            };

        [Serializable]
        public class SampleSettings
        {
            public CommonSettings Common;
            public FishAdderAndRemoverSettings FishAdderAndRemover;
            public IdleBobSystemSettings IdleBob;
        }
    }
}
