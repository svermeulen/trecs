// Companion docs: https://svermeulen.github.io/trecs/samples/10-pointers/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Demonstrates heap pointers: SharedPtr and UniquePtr.
    ///
    /// Entities patrol along shared waypoint routes (SharedPtr&lt;PatrolRoute&gt;),
    /// each leaving a unique trail of recent positions (UniquePtr&lt;TrailHistory&gt;).
    ///
    /// Both pointer types store managed data (List&lt;Vector3&gt;) that cannot live
    /// in unmanaged struct components — this is the core reason pointers exist.
    ///
    /// Key concepts:
    /// 1. SharedPtr — reference-counted shared objects (Clone to distribute)
    /// 2. UniquePtr — exclusive mutable per-entity objects
    /// 3. Pointer disposal — OnRemoved event cleans up when entities are destroyed
    /// </summary>
    public class PointersCompositionRoot : CompositionRootBase
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

            // SharedPtr/UniquePtr require a writable blob store. The in-memory
            // store is sufficient for samples — no on-disk persistence needed.
            var blobStore = new BlobStoreInMemory(
                new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                null
            );

            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddTemplate(SampleTemplates.PatrolFollowerEntity.Template)
                .AddBlobStore(blobStore)
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new PatrolMovementSystem(),
                    new PatrolRendererSystem(gameObjectRegistry),
                }
            );

            // Register cleanup handler BEFORE spawning, so it catches
            // all future removals (including world disposal).
            var followerCleanup = new PatrolFollowerCleanup(world);

            var sceneInitializer = new SceneInitializer(
                world,
                gameObjectRegistry,
                EntitiesPerRoute
            );

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { followerCleanup.Dispose, world.Dispose };
        }
    }
}
