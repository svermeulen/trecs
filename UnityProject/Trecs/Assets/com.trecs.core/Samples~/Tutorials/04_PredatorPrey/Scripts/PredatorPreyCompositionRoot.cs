// Companion docs: https://svermeulen.github.io/trecs/samples/04-predator-prey/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.PredatorPrey
{
    public class PredatorPreyCompositionRoot : CompositionRootBase
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
            var world = new WorldBuilder()
                .AddTemplates(
                    new[]
                    {
                        SampleTemplates.PredatorEntity.Template,
                        SampleTemplates.PreyEntity.Template,
                    }
                )
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new PredatorChoosePreySystem(),
                    new MovementSystem(),
                    new PredatorChaseSystem(),
                    new PreyRespawnSystem(Settings),
                    new MoverPresenter(goManager),
                }
            );

            var sceneInitializer = new SceneInitializer(world, Settings, goManager);

            initializables = new()
            {
                world.Initialize,
                sceneInitializer.Initialize,
                world.SubmitEntities,
            };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { goManager.Dispose, world.Dispose };
        }
    }

    [Serializable]
    public class SampleSettings
    {
        public int PredatorCount = 5;
        public int PreyCount = 20;
        public float PredatorSpeed = 3f;
        public float PreySpeed = 2f;
        public float SpawnRadius = 10f;
    }
}
