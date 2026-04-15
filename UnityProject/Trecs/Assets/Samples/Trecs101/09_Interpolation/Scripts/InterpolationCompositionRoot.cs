using System;
using System.Collections.Generic;
using Trecs.Internal;
using Unity.Mathematics;
using UnityEngine;

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
                        RandomSeed = 42,
                        // Low fixed rate to make interpolation difference clearly visible
                        FixedTimeStep = 0.1f, // 10 Hz
                    }
                )
                .AddTemplate(SampleTemplates.SmoothOrbitEntity.Template)
                .AddTemplate(SampleTemplates.RawOrbitEntity.Template)
                // Register the previous-frame saver for Position.
                // This copies Position → InterpolatedPrevious<Position>
                // before each fixed update step.
                .AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<Position>())
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    // Interpolation system: runs in variable update, computes
                    // Interpolated<Position> = lerp(previous, current, percent)
                    new InterpolatedUpdater<Position>(InterpolatePosition),
                    new OrbitSystem(),
                    new OrbitRendererSystem(gameObjectRegistry),
                }
            );

            initializables = new()
            {
                world.Initialize,
                () => SpawnEntities(world, gameObjectRegistry),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }

        /// <summary>
        /// The interpolation function: blends between previous and current
        /// Position values. Called each variable update frame.
        /// </summary>
        static void InterpolatePosition(
            in Position previous,
            in Position current,
            ref Position result,
            float percentThroughFixedFrame,
            WorldAccessor ecs
        )
        {
            result.Value = math.lerp(previous.Value, current.Value, percentThroughFixedFrame);
        }

        void SpawnEntities(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();
            const float ringOffsetX = 5f;

            for (int i = 0; i < EntitiesPerRing; i++)
            {
                float phase = (float)i / EntitiesPerRing * 2f * math.PI;

                // ── Left ring: Smooth (interpolated) ──
                {
                    var position = new float3(
                        -ringOffsetX + math.cos(phase) * OrbitRadius,
                        0.5f,
                        math.sin(phase) * OrbitRadius
                    );

                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = $"Smooth_{i}";
                    go.transform.position = (Vector3)position;
                    go.transform.localScale = Vector3.one * 0.6f;
                    go.GetComponent<Renderer>().material.color = Color.green;

                    // SetInterpolated sets Position, Interpolated<Position>,
                    // and InterpolatedPrevious<Position> all at once.
                    ecs.AddEntity<OrbitTags.Smooth>()
                        .SetInterpolated(new Position(position))
                        .Set(
                            new OrbitParams
                            {
                                Radius = OrbitRadius,
                                Speed = OrbitSpeed,
                                Phase = phase,
                                CenterX = -ringOffsetX,
                            }
                        )
                        .Set(gameObjectRegistry.Register(go))
                        .AssertComplete();
                }

                // ── Right ring: Raw (not interpolated) ──
                {
                    var position = new float3(
                        ringOffsetX + math.cos(phase) * OrbitRadius,
                        0.5f,
                        math.sin(phase) * OrbitRadius
                    );

                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = $"Raw_{i}";
                    go.transform.position = (Vector3)position;
                    go.transform.localScale = Vector3.one * 0.6f;
                    go.GetComponent<Renderer>().material.color = Color.red;

                    ecs.AddEntity<OrbitTags.Raw>()
                        .Set(new Position(position))
                        .Set(
                            new OrbitParams
                            {
                                Radius = OrbitRadius,
                                Speed = OrbitSpeed,
                                Phase = phase,
                                CenterX = ringOffsetX,
                            }
                        )
                        .Set(gameObjectRegistry.Register(go))
                        .AssertComplete();
                }
            }
        }
    }
}
