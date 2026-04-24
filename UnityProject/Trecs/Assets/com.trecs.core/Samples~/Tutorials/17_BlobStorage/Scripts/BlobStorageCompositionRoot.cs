// Companion docs: https://svermeulen.github.io/trecs/samples/17-blob-storage/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.BlobStorage
{
    public class BlobStorageCompositionRoot : CompositionRootBase
    {
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var registry = new GameObjectRegistry();

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.SwatchEntity.Template)
                .Build();

            world.AddSystems(
                new ISystem[] { new PaletteCycleSystem(), new SwatchRendererSystem(registry) }
            );

            var seeder = new PaletteSeeder(world);
            var sceneInitializer = new SceneInitializer(world, registry);

            // Order matters: world.Initialize first, then seed the heap, then
            // spawn entities that look up the seeded blobs by ID.
            initializables = new()
            {
                world.Initialize,
                seeder.Initialize,
                sceneInitializer.Initialize,
            };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { seeder.Dispose, world.Dispose };
        }
    }
}
