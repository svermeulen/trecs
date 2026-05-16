// Companion docs: https://svermeulen.github.io/trecs/samples/02-spawn-and-destroy/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.SpawnAndDestroy
{
    public class SpawnAndDestroyCompositionRoot : CompositionRootBase
    {
        public float SpawnInterval = 0.5f;
        public float Lifetime = 3f;
        public float SpawnRadius = 5f;

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var world = new WorldBuilder()
                .AddTemplates(
                    new[]
                    {
                        SampleTemplates.SampleGlobals.Template,
                        SampleTemplates.SphereEntity.Template,
                    }
                )
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(SpawnInterval, Lifetime, SpawnRadius),
                    new LifetimeSystem(),
                    new SpherePresenter(goManager),
                }
            );

            var sceneInitializer = new SceneInitializer(goManager);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { goManager.Dispose, world.Dispose };
        }
    }
}
