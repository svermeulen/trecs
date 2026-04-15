using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    public class PredatorPreyCompositionRoot : CompositionRootBase
    {
        public int PredatorCount = 5;
        public int PreyCount = 20;
        public float PredatorSpeed = 3f;
        public float SpawnRadius = 10f;

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
                .AddTemplate(SampleTemplates.PredatorEntity.Template)
                .AddTemplate(SampleTemplates.PreyEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new ChaseSystem(),
                    new MovementSystem(),
                    new CatchSystem(gameObjectRegistry),
                    new PreyRespawnSystem(PreyCount, SpawnRadius, gameObjectRegistry),
                    new EntityRendererSystem(gameObjectRegistry),
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

        void SpawnEntities(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();
            var rng = ecs.FixedRng;

            for (int i = 0; i < PredatorCount; i++)
            {
                float angle = rng.Next() * 2f * math.PI;
                var position = new float3(
                    rng.NextFloat(-SpawnRadius, SpawnRadius),
                    0.5f,
                    rng.NextFloat(-SpawnRadius, SpawnRadius)
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = $"Predator_{i}";
                go.transform.localScale = new Vector3(0.6f, 0.6f, 1.2f);
                go.transform.position = (Vector3)position;
                go.GetComponent<Renderer>().material.color = Color.red;

                ecs.AddEntity<SampleTags.Predator>()
                    .Set(new Position(position))
                    .Set(new Velocity(new float3(math.cos(angle), 0, math.sin(angle))))
                    .Set(new Speed(PredatorSpeed))
                    .Set(gameObjectRegistry.Register(go))
                    .AssertComplete();
            }

            for (int i = 0; i < PreyCount; i++)
            {
                var position = new float3(
                    rng.NextFloat(-SpawnRadius, SpawnRadius),
                    0.5f,
                    rng.NextFloat(-SpawnRadius, SpawnRadius)
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Prey_{i}";
                go.transform.position = (Vector3)position;
                go.GetComponent<Renderer>().material.color = Color.cyan;

                ecs.AddEntity<SampleTags.Prey>()
                    .Set(new Position(position))
                    .Set(gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
