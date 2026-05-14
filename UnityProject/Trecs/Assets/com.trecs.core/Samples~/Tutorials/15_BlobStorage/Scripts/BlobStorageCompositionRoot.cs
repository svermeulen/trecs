// Companion docs: https://svermeulen.github.io/trecs/samples/17-blob-storage/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.BlobStorage
{
    public class BlobStorageCompositionRoot : CompositionRootBase
    {
        // All we do here is call constructors and set up dependencies
        // between classes.  No initialization logic otherwise
        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );

            var world = new WorldBuilder()
                .AddTemplate(SampleTemplates.SwatchEntity.Template)
                .AddBlobStore(blobStore)
                .Build();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[] { new PaletteCycleSystem(), new SwatchPresenter(goManager) }
            );

            var seeder = new PaletteSeeder(world);
            var sceneInitializer = new SceneInitializer(world, goManager);

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
            disposables = new() { seeder.Dispose, goManager.Dispose, world.Dispose };
        }
    }
}
