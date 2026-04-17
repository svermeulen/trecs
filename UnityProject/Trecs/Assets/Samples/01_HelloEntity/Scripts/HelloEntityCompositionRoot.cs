// Companion docs: https://svermeulen.github.io/trecs/samples/01-hello-entity/
using System;
using System.Collections.Generic;

namespace Trecs.Samples.HelloEntity
{
    public class HelloEntityCompositionRoot : CompositionRootBase
    {
        public float RotationSpeed = 2f;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            // Note that GameObjectRegistry is from sample code and not a built
            // in trecs concept
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.SpinnerEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new SpinnerSystem(RotationSpeed),
                    new SpinnerGameObjectUpdater(gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
