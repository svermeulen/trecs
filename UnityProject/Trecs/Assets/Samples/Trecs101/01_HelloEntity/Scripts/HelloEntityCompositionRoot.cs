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
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddTemplate(SampleTemplates.SpinnerEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new SpinnerSystem(RotationSpeed),
                    new SpinnerGameObjectUpdater(gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry);

            initializables = new()
            {
                world.Initialize,
                sceneInitializer.Initialize,
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
