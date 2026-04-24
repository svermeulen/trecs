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

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var registry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityTypes(
                    new[]
                    {
                        SampleTemplates.Globals.Template,
                        SampleTemplates.BubbleEntity.Template,
                    }
                )
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new BubbleSpawnerSystem(SpawnInterval, registry),
                    new BubblePhysicsSystem(),
                    new BubbleLifetimeSystem(),
                    new BubbleRendererSystem(registry),
                }
            );

            var observer = new GameStatsUpdater(world, registry);

            world.AddSystem(new TextDisplaySystem(StatusText));

            initializables = new() { world.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { observer.Dispose, world.Dispose };
        }
    }
}
