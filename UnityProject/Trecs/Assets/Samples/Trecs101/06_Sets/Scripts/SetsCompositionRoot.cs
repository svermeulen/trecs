using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Filters
{
    /// <summary>
    /// Demonstrates persistent sets: sparse entity subsets within a group.
    ///
    /// Particles are spawned in a grid. A HighlightSystem adds/removes
    /// particles from a set based on a sine wave. The HighlightRendererSystem
    /// iterates only the set's subset to update their color — without needing
    /// to check every particle.
    /// </summary>
    public class SetsCompositionRoot : CompositionRootBase
    {
        public int GridSize = 10;
        public float Spacing = 1.2f;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddTemplate(SampleTemplates.ParticleEntity.Template)
                .AddSet<SampleSets.HighlightedParticle>()
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new HighlightSystem(GridSize),
                    new HighlightRendererSystem(gameObjectRegistry),
                }
            );

            initializables = new()
            {
                world.Initialize,
                () => SpawnParticles(world, gameObjectRegistry),
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }

        void SpawnParticles(World world, GameObjectRegistry gameObjectRegistry)
        {
            var ecs = world.CreateAccessor();
            var offset = (GridSize - 1) * Spacing * 0.5f;

            for (int x = 0; x < GridSize; x++)
            {
                for (int z = 0; z < GridSize; z++)
                {
                    var position = new float3(x * Spacing - offset, 0.5f, z * Spacing - offset);

                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = $"Particle_{x}_{z}";
                    go.transform.position = (Vector3)position;
                    go.transform.localScale = Vector3.one * 0.8f;
                    go.GetComponent<Renderer>().material.color = Color.gray;

                    ecs.AddEntity<SampleTags.Particle>()
                        .Set(new Position(position))
                        .Set(new Lifetime(0))
                        .Set(gameObjectRegistry.Register(go))
                        .AssertComplete();
                }
            }
        }
    }
}
