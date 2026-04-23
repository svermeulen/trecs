// Companion docs: https://svermeulen.github.io/trecs/samples/14-native-pointers/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.NativePointers
{
    /// <summary>
    /// Demonstrates the Burst-compatible heap pointers: NativeSharedPtr and NativeUniquePtr.
    ///
    /// Mirrors Sample 10 (Pointers) but with every heap payload as an unmanaged struct,
    /// so the movement system can resolve the pointers inside a Burst-compiled job via
    /// <c>NativeWorldAccessor</c>. The managed SharedPtr / UniquePtr from Sample 10
    /// cannot be accessed from jobs at all — that is the reason these native variants exist.
    ///
    /// Key concepts:
    /// 1. NativeSharedPtr — reference-counted shared blob, resolved in-job with <c>Get(world)</c>
    /// 2. NativeUniquePtr — exclusive per-entity blob, mutated in-job with <c>GetMut(world)</c>
    /// 3. Pointer disposal — OnRemoved event disposes pointers when entities are destroyed
    /// </summary>
    public class NativePointersCompositionRoot : CompositionRootBase
    {
        public int EntitiesPerRoute = 3;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var gameObjectRegistry = new GameObjectRegistry();

            // Native pointers use the same blob store as managed pointers —
            // the in-memory store is sufficient for samples.
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );

            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddEntityType(SampleTemplates.NativePatrolFollowerEntity.Template)
                .AddBlobStore(blobStore)
                .Build();

            var cleanupHandler = new PointerCleanupHandler(world);

            world.AddSystems(
                new ISystem[]
                {
                    new PatrolMovementSystem(),
                    new PatrolRendererSystem(gameObjectRegistry),
                }
            );

            var sceneInitializer = new SceneInitializer(
                world,
                gameObjectRegistry,
                EntitiesPerRoute
            );

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { cleanupHandler.Dispose, world.Dispose };
        }
    }
}
