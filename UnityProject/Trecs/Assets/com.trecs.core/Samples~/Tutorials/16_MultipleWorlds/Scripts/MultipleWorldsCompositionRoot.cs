// Companion docs: https://svermeulen.github.io/trecs/samples/16-multiple-worlds/

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    public class MultipleWorldsCompositionRoot : CompositionRootBase
    {
        public float SpawnIntervalA = 0.5f;
        public float SpawnIntervalB = 0.7f;
        public float Lifetime = 3f;
        public float SpawnRadius = 2f;
        public float WorldSeparation = 3f;

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // Each world gets its own RenderableGameObjectManager — the
            // manager's id counter lives in its world's heap so it must be
            // 1:1 with a World. GameObjectId values are world-local now,
            // not shared. Either world can be snapshotted independently
            // and its GameObjects will be rebuilt from its entity set.
            var worldA = new WorldBuilder()
                .SetDebugName("World A — Red Spheres")
                .AddTemplates(
                    new[]
                    {
                        SampleTemplates.SampleGlobals.Template,
                        SampleTemplates.CritterEntity.Template,
                    }
                )
                .Build();

            var goManagerA = new RenderableGameObjectManager(worldA);

            worldA.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(
                        SpawnIntervalA,
                        Lifetime,
                        SpawnRadius,
                        new Vector3(-WorldSeparation, 0, 0)
                    ),
                    new LifetimeSystem(),
                    new PrimitivePresenter(goManagerA),
                }
            );

            var worldB = new WorldBuilder()
                .SetDebugName("World B — Blue Cubes")
                .AddTemplates(
                    new[]
                    {
                        SampleTemplates.SampleGlobals.Template,
                        SampleTemplates.CritterEntity.Template,
                    }
                )
                .Build();

            var goManagerB = new RenderableGameObjectManager(worldB);

            worldB.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(
                        SpawnIntervalB,
                        Lifetime,
                        SpawnRadius,
                        new Vector3(WorldSeparation, 0, 0)
                    ),
                    new LifetimeSystem(),
                    new PrimitivePresenter(goManagerB),
                }
            );

            var sceneInitA = new SceneInitializer(goManagerA, PrimitiveType.Sphere, Color.red);
            var sceneInitB = new SceneInitializer(goManagerB, PrimitiveType.Cube, Color.blue);

            initializables = new()
            {
                worldA.Initialize,
                worldB.Initialize,
                sceneInitA.Initialize,
                sceneInitB.Initialize,
            };

            tickables = new() { worldA.Tick, worldB.Tick };

            lateTickables = new() { worldA.LateTick, worldB.LateTick };

            disposables = new()
            {
                goManagerA.Dispose,
                goManagerB.Dispose,
                worldA.Dispose,
                worldB.Dispose,
            };
        }
    }
}
