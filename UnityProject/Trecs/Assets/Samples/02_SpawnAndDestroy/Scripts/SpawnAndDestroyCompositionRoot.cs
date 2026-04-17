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

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.SphereEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new SpawnSystem(SpawnInterval, Lifetime, SpawnRadius, gameObjectRegistry),
                    new LifetimeSystem(gameObjectRegistry),
                    new SphereRendererSystem(gameObjectRegistry),
                }
            );

            initializables = new() { world.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
