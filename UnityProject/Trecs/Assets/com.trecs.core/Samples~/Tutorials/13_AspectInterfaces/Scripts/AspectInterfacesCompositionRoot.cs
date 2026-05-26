// Companion docs: https://svermeulen.github.io/trecs/samples/15-aspect-interfaces/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.AspectInterfaces
{
    public class AspectInterfacesCompositionRoot : CompositionRootBase
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
                .AddTemplate(SampleTemplates.EnemyEntity.Template)
                .AddTemplate(SampleTemplates.BossEntity.Template)
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[]
                {
                    new EnemyAiSystem(Settings),
                    new BossAiSystem(Settings),
                    new HitFlashPresenter(goManager, Settings),
                }
            );

            var sceneInitializer = new SceneInitializer(world, Settings, goManager);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { goManager.Dispose, world.Dispose };
        }
    }
}
