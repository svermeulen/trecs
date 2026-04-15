using System;
using System.Collections.Generic;
using TMPro;
using Trecs.Internal;
using Trecs.Serialization;
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
                .AddTemplates(GetTemplates(config));

            if (config.StateApproach == FrenzyStateApproach.Sets)
            {
                worldBuilder.AddSet<FrenzySets.Eating>().AddSet<FrenzySets.NotEating>();
            }

            var world = worldBuilder.Build();

            var stateApproachDynamicSwitcher = new StateApproachDynamicSwitcher(world);
            var serialization = TrecsSerialization.Create(world);
            var recordAndPlayback = new RecordAndPlaybackController(
                serialization,
                world,
                sampleName: "FeedingFrenzy"
            );

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

            if (Settings.Common.RenderingEnabled)
            {
                var renderer = new RendererSystem();

                var fishMesh = SampleUtil.CreateDartMesh();
                var mealMesh = SampleUtil.CreateScaledCubeMesh();
                var fishMaterial = SampleUtil.CreateUnlitIndirectMaterial(
                    Settings.Common.FishColor
                );
                var mealMaterial = SampleUtil.CreateUnlitIndirectMaterial(
                    Settings.Common.MealColor
                );

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
            }

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

            initializables = new()
            {
                world.Initialize,
                sceneInitializer.Initialize,
            };

            tickables = new()
            {
                configInput.Tick,
                presetInput.Tick,
                recordAndPlayback.Tick,
                world.Tick,
                stateApproachDynamicSwitcher.Tick,
            };

            lateTickables = new() { world.LateTick };

            disposables = new()
            {
                recordAndPlayback.Dispose,
                fishSetInitializer.Dispose,
                removeCleanupHandler.Dispose,
                perfStats.Dispose,
                world.Dispose,
                serialization.Dispose,
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
            if (config.StateApproach == FrenzyStateApproach.States)
            {
                return new Template[]
                {
                    Templates.StatesFishEntity.Template,
                    Templates.StatesMealEntity.Template,
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
            config.StateApproach switch
            {
                FrenzyStateApproach.Branching => new Branching.LookingForMealSystem(),
                FrenzyStateApproach.Sets => new Sets.LookingForMealSystem(),
                FrenzyStateApproach.States => new States.LookingForMealSystem(),
                _ => throw new ArgumentOutOfRangeException(),
            };

        static ISystem CreateConsumingMealSystem(FrenzyConfigSettings config) =>
            config.StateApproach switch
            {
                FrenzyStateApproach.Branching => new Branching.ConsumingMealSystem(),
                FrenzyStateApproach.Sets => new Sets.ConsumingMealSystem(),
                FrenzyStateApproach.States => new States.ConsumingMealSystem(),
                _ => throw new ArgumentOutOfRangeException(),
            };

        static ISystem CreateMovementSystem(FrenzyConfigSettings config) =>
            config.StateApproach switch
            {
                FrenzyStateApproach.Branching => new Branching.MovementSystem(),
                FrenzyStateApproach.Sets => new Sets.MovementSystem(),
                FrenzyStateApproach.States => new States.MovementSystem(),
                _ => throw new ArgumentOutOfRangeException(),
            };

        static ISystem CreateIdleBobSystem(
            FrenzyConfigSettings config,
            IdleBobSystemSettings settings
        ) =>
            config.StateApproach switch
            {
                FrenzyStateApproach.Branching => new Branching.IdleBobSystem(settings),
                FrenzyStateApproach.Sets => new Sets.IdleBobSystem(settings),
                FrenzyStateApproach.States => new States.IdleBobSystem(settings),
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
