// Companion docs: https://svermeulen.github.io/trecs/samples/03-aspects/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.Aspects
{
    public class AspectsCompositionRoot : CompositionRootBase
    {
        public SampleSettings Settings;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.BoidEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new BoidMovementSystem(),
                    new BoidWrapSystem(Settings.AreaSize),
                    new BoidRendererSystem(gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry, Settings);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }

    [Serializable]
    public class SampleSettings
    {
        public int BoidCount = 50;
        public float AreaSize = 20f;
        public float MinSpeed = 1f;
        public float MaxSpeed = 3f;
    }
}
