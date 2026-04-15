using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Simplified FeedingFrenzy sample demonstrating best-practice patterns:
    /// States, Aspects, Jobs ([WrapAsJob] and [BurstCompile]), and
    /// NativeComponentLookupRead for cross-entity access.
    ///
    /// Uses GPU indirect rendering (RendererSystem) to scale to hundreds
    /// of thousands of entities. Entity counts are controlled at runtime
    /// via keyboard (Up/Down arrows).
    ///
    /// Fish swim toward meals, eat them (growing based on meal nutrition),
    /// and shrink over time from hunger. If a fish shrinks too much it
    /// starves and is removed. This creates a full entity lifecycle:
    /// spawn → idle → eat (grow) → shrink → starve (removed).
    /// </summary>
    public class FeedingFrenzyCompositionRoot : CompositionRootBase
    {
        public int MaxFishCount = 200_000;
        public int MaxMealCount = 200_000;
        public float MealCountRatio = 0.75f;
        public float SpawnSpread = 100f;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // ─── GPU indirect rendering ─────────────────────────────
            var renderer = new RendererSystem();

            // Fish color is driven per-entity by StarvationSystem via ColorComponent
            // (cyan = healthy, red-orange = starving). Material base color is
            // white so per-instance color passes through unmodified.
            renderer.RegisterRenderable(
                TagSet<FrenzyTags.Fish>.Value,
                SampleUtil.CreateDartMesh(),
                SampleUtil.CreateUnlitIndirectMaterial(Color.white),
                MaxFishCount
            );

            renderer.RegisterRenderable(
                TagSet<FrenzyTags.Meal>.Value,
                SampleUtil.CreateScaledCubeMesh(),
                SampleUtil.CreateUnlitIndirectMaterial(Color.green),
                MaxMealCount
            );

            // ─── World setup ────────────────────────────────────────
            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddTemplate(SampleTemplates.Globals.Template)
                .AddTemplate(SampleTemplates.FishEntity.Template)
                .AddTemplate(SampleTemplates.MealEntity.Template)
                .Build();

            var cleanupHandler = new RemoveCleanupHandler(world);

            world.AddSystems(
                new ISystem[]
                {
                    new CountInputSystem(),
                    new FishAdderAndRemover(MealCountRatio, SpawnSpread),
                    new MealAdderAndRemover(SpawnSpread),
                    new LookingForMealSystem(),
                    new ConsumingMealSystem(),
                    new MovementSystem(),
                    new IdleBobSystem(),
                    new StarvationSystem(),
                    new VisualSmoothingSystem(),
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
