// Companion docs: https://svermeulen.github.io/trecs/samples/18-reactive-events/

using System;
using System.Collections.Generic;
using TMPro;

namespace Trecs.Samples.ReactiveEvents
{
    public class ReactiveEventsCompositionRoot : CompositionRootBase
    {
        public float SpawnInterval = 0.3f;
        public TMP_Text StatusText;

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
                        SampleTemplates.Globals.Template,
                        SampleTemplates.BubbleEntity.Template,
                    }
                )
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new BubbleSpawnerSystem(SpawnInterval),
                    new BubblePhysicsSystem(),
                    new BubbleLifetimeSystem(),
                    new BubblePresenter(goManager),
                }
            );

            var observer = new GameStatsUpdater(world);

            world.AddSystem(new TextDisplaySystem(StatusText));

            var sceneInitializer = new SceneInitializer(goManager);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { observer.Dispose, goManager.Dispose, world.Dispose };
        }
    }
}
