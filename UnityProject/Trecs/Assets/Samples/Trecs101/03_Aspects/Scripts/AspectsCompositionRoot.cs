using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Aspects
{
    public class AspectsCompositionRoot : CompositionRootBase
    {
        public int BoidCount = 50;
        public float AreaSize = 20f;
        public float MinSpeed = 1f;
        public float MaxSpeed = 3f;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 790485328 })
                .AddTemplate(SampleTemplates.BoidEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new BoidMovementSystem(),
                    new BoidWrapSystem(AreaSize),
                    new BoidRendererSystem(gameObjectRegistry),
                }
            );

            initializables = new()
            {
                world.Initialize,
                () => SpawnBoids(world, gameObjectRegistry),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }

        void SpawnBoids(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();
            var rng = ecs.FixedRng;
            float halfSize = AreaSize / 2f;

            for (int i = 0; i < BoidCount; i++)
            {
                float angle = rng.Next() * 2f * math.PI;
                var velocity = new float3(math.cos(angle), 0, math.sin(angle));
                float speed = MinSpeed + rng.Next() * (MaxSpeed - MinSpeed);
                var position = new float3(
                    rng.NextFloat(-halfSize, halfSize),
                    0.25f,
                    rng.NextFloat(-halfSize, halfSize)
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = $"Boid_{i}";
                go.transform.localScale = new Vector3(0.3f, 0.3f, 0.6f);
                go.transform.position = (Vector3)position;

                var renderer = go.GetComponent<Renderer>();
                renderer.material.color = Color.HSVToRGB(rng.Next(), 0.7f, 0.9f);

                ecs.AddEntity<SampleTags.Boid>()
                    .Set(new Position(position))
                    .Set(new Velocity(velocity))
                    .Set(new Speed(speed))
                    .Set(gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
