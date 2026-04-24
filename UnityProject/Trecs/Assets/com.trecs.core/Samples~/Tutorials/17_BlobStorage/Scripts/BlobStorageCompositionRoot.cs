// Companion docs: https://svermeulen.github.io/trecs/samples/17-blob-storage/

using System;
using System.Collections.Generic;
using Trecs.Internal;

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

            // Register a writable in-memory blob store. Any implementation of
            // IBlobStore works here — this is the extension point you'd use to
            // load blobs from disk, asset bundles, or a network source.
            var store = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                poolManager: null
            );

            var world = new WorldBuilder()
                .AddEntityType(SampleTemplates.SwatchEntity.Template)
                .AddBlobStore(store)
                .Build();

            // BlobCache is currently surfaced via a Trecs.Internal extension;
            // this is the world-wide service used to create, retrieve, and
            // dispose blob handles.
            var blobCache = world.GetBlobCache();

            world.AddSystems(
                new ISystem[]
                {
                    new PaletteCycleSystem(blobCache),
                    new SwatchRendererSystem(registry),
                }
            );

            var sceneInitializer = new SceneInitializer(world, blobCache, registry);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { world.Dispose };
        }
    }
}
