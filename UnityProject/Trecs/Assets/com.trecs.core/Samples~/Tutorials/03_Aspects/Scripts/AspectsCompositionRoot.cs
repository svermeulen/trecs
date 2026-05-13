// Companion docs: https://svermeulen.github.io/trecs/samples/03-aspects/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.Aspects
{
    public class AspectsCompositionRoot : CompositionRootBase
    {
        public SampleSettings Settings;

        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var world = new WorldBuilder().AddTemplate(SampleTemplates.BoidEntity.Template).Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new BoidMovementSystem(),
                    new BoidWrapSystem(Settings.AreaSize),
                    new BoidPresenter(goManager),
                }
            );

            var sceneInitializer = new SceneInitializer(world, Settings, goManager);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { goManager.Dispose, world.Dispose };
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
