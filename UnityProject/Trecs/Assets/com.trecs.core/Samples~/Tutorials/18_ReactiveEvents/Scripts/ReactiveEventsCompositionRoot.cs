// Companion docs: https://svermeulen.github.io/trecs/samples/18-reactive-events/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.ReactiveEvents
{
    public class ReactiveEventsCompositionRoot : CompositionRootBase
    {
        public float SpawnInterval = 0.3f;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var registry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.BubbleEntity.Template)
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

            var observer = new EventObserverInstaller(world, registry);

            // The observer must be installed AFTER world.Initialize so that the
            // world's accessor is ready, but BEFORE any entities are spawned
            // so that the first submission fires OnAdded for us. Ordering it
            // after world.Initialize in the initializables list gets this right.
            initializables = new() { world.Initialize, observer.Install };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { observer.Dispose, world.Dispose };
        }
    }
}
