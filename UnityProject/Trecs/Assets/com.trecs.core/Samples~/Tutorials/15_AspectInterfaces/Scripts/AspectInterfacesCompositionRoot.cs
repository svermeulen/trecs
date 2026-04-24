// Companion docs: https://svermeulen.github.io/trecs/samples/15-aspect-interfaces/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.AspectInterfaces
{
    public class AspectInterfacesCompositionRoot : CompositionRootBase
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
                .AddEntityType(SampleTemplates.EnemyEntity.Template)
                .AddEntityType(SampleTemplates.BossEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new EnemyAiSystem(Settings),
                    new BossAiSystem(Settings),
                    new HitFlashRenderer(gameObjectRegistry, Settings),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry, Settings);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
