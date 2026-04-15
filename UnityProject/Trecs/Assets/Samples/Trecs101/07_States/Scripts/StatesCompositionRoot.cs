using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.States
{
    /// <summary>
    /// Demonstrates template states: entities grouped by state tag for
    /// cache-friendly iteration.
    ///
    /// Balls bounce around with physics (Active state) then come to rest
    /// on the ground (Resting state). After a timer, resting balls launch
    /// back up. The physics system only touches Active balls — which are
    /// stored contiguously in memory, giving optimal cache performance.
    /// </summary>
    public class StatesCompositionRoot : CompositionRootBase
    {
        public int BallCount = 100;
        public float SpawnRadius = 8f;

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
                .AddTemplate(SampleTemplates.BallEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new PhysicsSystem(),
                    new WakeUpSystem(),
                    new BallRendererSystem(gameObjectRegistry),
                }
            );

            initializables = new()
            {
                world.Initialize,
                () => SpawnBalls(world, gameObjectRegistry),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }

        void SpawnBalls(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();
            var rng = ecs.FixedRng;

            for (int i = 0; i < BallCount; i++)
            {
                var position = new float3(
                    rng.NextFloat(-SpawnRadius, SpawnRadius),
                    rng.NextFloat(3f, 15f),
                    rng.NextFloat(-SpawnRadius, SpawnRadius)
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Ball_{i}";
                go.transform.position = (Vector3)position;
                go.transform.localScale = Vector3.one * 0.6f;

                // Balls start in Active state — they'll fall under gravity
                ecs.AddEntity<BallTags.Ball, BallTags.Active>()
                    .Set(new Position(position))
                    .Set(new Velocity(float3.zero))
                    .Set(new RestTimer(0f))
                    .Set(gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
