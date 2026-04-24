// Companion docs: https://svermeulen.github.io/trecs/samples/16-data-driven-templates/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.DataDrivenTemplates
{
    public class DataDrivenCompositionRoot : CompositionRootBase
    {
        public ArchetypeLibrary Library;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            if (Library == null)
            {
                throw new InvalidOperationException(
                    "DataDrivenCompositionRoot.Library is not assigned. Create an "
                        + "ArchetypeLibrary asset (Assets > Create > Trecs Samples > Archetype Library) "
                        + "and drop it into the Library field in the inspector."
                );
            }

            var gameObjectRegistry = new GameObjectRegistry();

            // Build Templates from the data file at startup.
            var built = ArchetypeLoader.BuildAll(Library);

            var worldBuilder = new WorldBuilder();
            foreach (var archetype in built)
            {
                worldBuilder.AddEntityType(archetype.Template);
            }
            var world = worldBuilder.Build();

            world.AddSystems(
                new ISystem[]
                {
                    new SpinnerSystem(),
                    new OrbiterSystem(),
                    new BobberSystem(),
                    new GameObjectUpdater(gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, gameObjectRegistry, built.ToArray());

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
