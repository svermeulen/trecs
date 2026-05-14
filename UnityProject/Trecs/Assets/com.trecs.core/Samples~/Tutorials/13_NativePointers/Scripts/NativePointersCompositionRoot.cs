// Companion docs: https://svermeulen.github.io/trecs/samples/14-native-pointers/

using System;
using System.Collections.Generic;
using Trecs.Serialization;

namespace Trecs.Samples.NativePointers
{
    /// <summary>
    /// Introduction to Burst-compatible heap pointers via
    /// <see cref="NativeUniquePtr{T}"/>.
    ///
    /// Several followers chase a hard-coded figure-8 path, each appending its
    /// current position to a per-entity <see cref="TrailHistory"/>. The trail
    /// is an unmanaged struct (<see cref="Trecs.Collections.FixedList64{T}"/>
    /// of <see cref="Unity.Mathematics.float3"/>), so it can live in Trecs'
    /// native heap and the movement system can resolve and mutate it from
    /// inside a Burst-compiled job via <c>NativeWorldAccessor</c> — that's
    /// the whole reason to reach for the native pointer variants instead of
    /// the managed ones in Sample 10.
    ///
    /// <see cref="Trecs.Internal.NativeUniqueHeap"/> writes each blob's
    /// inner type id during snapshot save — so the Player window needs a
    /// per-world serializer registry with <see cref="TrailHistory"/>
    /// registered, or the save fails. We register it via
    /// <see cref="BlitSerializer{T}"/> (the struct is unmanaged) — no
    /// hand-written <see cref="ISerializer{T}"/> needed.
    ///
    /// <see cref="NativeSharedPtr{T}"/> is a more advanced topic and is
    /// intentionally NOT used here — a follow-up sample can introduce
    /// sharing a heap blob across entities.
    /// </summary>
    public class NativePointersCompositionRoot : CompositionRootBase
    {
        public int FollowerCount = 5;

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
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddTemplate(SampleTemplates.NativePatrolFollowerEntity.Template)
                .Build();

            // Register TrailHistory with the serializer registry so its
            // type id round-trips through snapshot save / load. The struct
            // is unmanaged, so a BlitSerializer is enough — no custom
            // ISerializer<TrailHistory> needed.
            world.SerializerRegistry.RegisterSerializer(new BlitSerializer<TrailHistory>());

            var goManager = new RenderableGameObjectManager(world);

            var cleanupHandler = new PointerCleanupHandler(world);

            world.AddSystems(
                new ISystem[] { new PatrolMovementSystem(), new PatrolPresenter(goManager) }
            );

            var sceneInitializer = new SceneInitializer(world, FollowerCount, goManager);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { cleanupHandler.Dispose, goManager.Dispose, world.Dispose };
        }
    }
}
