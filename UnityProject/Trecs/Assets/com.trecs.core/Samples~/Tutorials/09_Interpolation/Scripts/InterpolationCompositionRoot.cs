// Companion docs: https://svermeulen.github.io/trecs/samples/09-interpolation/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.Interpolation
{
    /// <summary>
    /// Demonstrates fixed-to-variable interpolation for smooth rendering.
    ///
    /// Two rings of cubes orbit a center point. Both are driven by the same
    /// fixed-update physics (OrbitSystem), but they render differently:
    ///
    /// LEFT RING (green) — Interpolated: reads Interpolated&lt;Position&gt;,
    /// which blends between the previous and current fixed-frame positions.
    /// Motion appears perfectly smooth regardless of frame rate.
    ///
    /// RIGHT RING (red) — Raw: reads Position directly from fixed update.
    /// Motion appears jittery because fixed updates run at a lower rate
    /// than rendering (10 Hz fixed vs 60+ Hz rendering in this sample).
    ///
    /// The fixed timestep is intentionally set to 0.1s (10 Hz) to make
    /// the difference clearly visible. In production, a smaller timestep
    /// (e.g. 0.02s / 50 Hz) would make raw motion less visibly jittery,
    /// but interpolation still provides smoother results.
    /// </summary>
    public class InterpolationCompositionRoot : CompositionRootBase
    {
        public int EntitiesPerRing = 8;
        public float OrbitRadius = 3f;
        public float OrbitSpeed = 2f;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .SetSettings(
                    new WorldSettings
                    {
                        // Low fixed rate to make interpolation difference clearly visible
                        FixedTimeStep = 0.1f, // 10 Hz
                    }
                )
                .AddEntityTypes(
                    new[]
                    {
                        SampleTemplates.SmoothOrbitEntity.Template,
                        SampleTemplates.RawOrbitEntity.Template,
                    }
                )
                .AddInterpolationSampleInterpolators()
                .Build();

            world.AddSystems(
                new ISystem[] { new OrbitSystem(), new OrbitRendererSystem(gameObjectRegistry) }
            );

            var sceneInitializer = new SceneInitializer(
                world,
                gameObjectRegistry,
                EntitiesPerRing,
                OrbitRadius,
                OrbitSpeed
            );

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
