using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Events
{
    /// <summary>
    /// Demonstrates the events/observer system: subscribing to entity
    /// lifecycle events (added, removed, moved), priority ordering,
    /// and proper disposal.
    ///
    /// Cubes spawn, grow to full size (Growing state), then shrink
    /// (Shrinking state) and get destroyed. Event observers react to
    /// each lifecycle stage with color changes and console output.
    /// </summary>
    public class EventsCompositionRoot : CompositionRootBase
    {
        public int CubeCount = 20;
        public float SpawnRadius = 8f;

        EventTracker _eventTracker;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddTemplate(SampleTemplates.CubeEntity.Template)
                .Build();

            // Subscribe to events BETWEEN Build() and Initialize().
            // This is the recommended time to register observers so they
            // catch all entity lifecycle events from the start.
            _eventTracker = new EventTracker(world, gameObjectRegistry);

            world.AddSystems(
                new ISystem[] { new LifecycleSystem(), new CubeRendererSystem(gameObjectRegistry) }
            );

            initializables = new()
            {
                world.Initialize,
                () => SpawnCubes(world, gameObjectRegistry),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { _eventTracker.Dispose, world.Dispose };
        }

        void SpawnCubes(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();

            for (int i = 0; i < CubeCount; i++)
            {
                var position = new float3(
                    ecs.FixedRng.NextFloat(-SpawnRadius, SpawnRadius),
                    0.5f,
                    ecs.FixedRng.NextFloat(-SpawnRadius, SpawnRadius)
                );

                // Random initial scale creates staggered lifecycles so
                // events fire at different times rather than all at once.
                float initialScale = ecs.FixedRng.NextFloat(0.1f, 0.8f);

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Cube_{i}";
                go.transform.position = (Vector3)position;
                go.transform.localScale = Vector3.one * initialScale;

                ecs.AddEntity<CubeTags.Cube, CubeTags.Growing>()
                    .Set(new Position(position))
                    .Set(new UniformScale(initialScale))
                    .Set(gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
