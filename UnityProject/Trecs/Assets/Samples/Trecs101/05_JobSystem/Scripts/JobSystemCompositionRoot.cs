using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.JobSystem
{
    public class JobSystemCompositionRoot : CompositionRootBase
    {
        public int ParticleCount = 5000;
        public float AreaSize = 20f;
        public float MaxSpeed = 5f;
        public float ParticleSize = 0.15f;
        public Color ParticleColor = new(0.2f, 0.8f, 1f);

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var mesh = CreateParticleMesh();
            var material = CreateParticleMaterial();

            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 12345 })
                .AddTemplate(SampleTemplates.ParticleEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new ParticleJobSystem(AreaSize),
                    new ParticleRendererSystem(mesh, material, ParticleSize),
                }
            );

            initializables = new()
            {
                world.Initialize,
                () => SpawnParticles(world),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }

        void SpawnParticles(World world)
        {
            var ecs = world.CreateAccessor();
            var rng = ecs.FixedRng;
            float halfSize = AreaSize / 2f;

            for (int i = 0; i < ParticleCount; i++)
            {
                var position = new float3(
                    rng.NextFloat(-halfSize, halfSize),
                    rng.NextFloat(0, halfSize),
                    rng.NextFloat(-halfSize, halfSize)
                );

                var velocity =
                    math.normalize(
                        new float3(
                            rng.NextFloat(-1f, 1f),
                            rng.NextFloat(-1f, 1f),
                            rng.NextFloat(-1f, 1f)
                        )
                    ) * rng.NextFloat(1f, MaxSpeed);

                ecs.AddEntity<SampleTags.Particle>()
                    .Set(new Position(position))
                    .Set(new Velocity(velocity))
                    .AssertComplete();
            }
        }

        Mesh CreateParticleMesh()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Destroy(go);
            return mesh;
        }

        Material CreateParticleMaterial()
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.color = ParticleColor;
            material.enableInstancing = true;
            return material;
        }
    }
}
