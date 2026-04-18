// Companion docs: https://svermeulen.github.io/trecs/samples/07-feeding-frenzy/

using System;
using System.Collections.Generic;
using TMPro;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzy101
{
    public class FeedingFrenzyCompositionRoot : CompositionRootBase
    {
        public int MinFishCount = 100;
        public int MaxFishCount = 200_000;
        public float MealCountRatio = 0.75f;
        public float SpawnSpread = 100f;
        public int MaxFishChangePerFrame = 50;
        public int MaxMealChangePerFrame = 50;
        public float3 MealScale;
        public TMP_Text NumEntitiesText;
        public Color MealColor;
        public StarvationSystem.Settings StarvationSystemSettings;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // ─── GPU instanced rendering ────────────────────────────
            var renderer = new RendererSystem();

            // Fish color is driven per-entity by StarvationSystem via ColorComponent
            // (cyan = healthy, red-orange = starving). Material base color is
            // white so per-instance color passes through unmodified.
            renderer.RegisterRenderable(
                TagSet<FrenzyTags.Fish>.Value,
                SampleUtil.CreateDartMesh(),
                SampleUtil.CreateUnlitInstancedMaterial(Color.white),
                MaxFishCount
            );

            renderer.RegisterRenderable(
                TagSet<FrenzyTags.Meal>.Value,
                SampleUtil.CreateScaledCubeMesh(MealScale.x, MealScale.y, MealScale.z),
                SampleUtil.CreateUnlitInstancedMaterial(MealColor),
                (int)(MaxFishCount * MealCountRatio + 0.5f)
            );

            // ─── World setup ────────────────────────────────────────
            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddEntityTypes(
                    new[]
                    {
                        SampleTemplates.Globals.Template,
                        SampleTemplates.FishEntity.Template,
                        SampleTemplates.MealEntity.Template,
                    }
                )
                .Build();

            var cleanupHandler = new RemoveCleanupHandler(world);

            world.AddSystems(
                new ISystem[]
                {
                    new InputSystem(MinFishCount, MaxFishCount),
                    new FishAdderAndRemover(MealCountRatio, SpawnSpread, MaxFishChangePerFrame),
                    new MealAdderAndRemover(SpawnSpread, MaxMealChangePerFrame),
                    new LookingForMealSystem(),
                    new ConsumingMealSystem(),
                    new MovementSystem(),
                    new IdleBobSystem(),
                    new StarvationSystem(StarvationSystemSettings),
                    new VisualSmoothingSystem(),
                    new TextDisplaySystem(NumEntitiesText),
                    renderer,
                }
            );

            initializables = new() { world.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { cleanupHandler.Dispose, renderer.Dispose, world.Dispose };
        }
    }
}
