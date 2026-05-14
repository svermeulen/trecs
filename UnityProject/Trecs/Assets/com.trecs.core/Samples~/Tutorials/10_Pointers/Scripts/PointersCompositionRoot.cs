// Companion docs: https://svermeulen.github.io/trecs/samples/10-pointers/

using System;
using System.Collections.Generic;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Introduction to managed heap pointers via <see cref="UniquePtr{T}"/>.
    ///
    /// Several followers chase a hard-coded figure-8 path, each leaving a
    /// trail of its recent positions behind it. Each trail is a
    /// <c>List&lt;Vector3&gt;</c> — a managed collection that cannot live
    /// directly in an unmanaged ECS component, so it sits behind a
    /// <see cref="UniquePtr{T}"/> on the entity.
    ///
    /// The managed <see cref="TrailHistory"/> payload needs a custom
    /// <see cref="ISerializer{T}"/> implementation to survive snapshot
    /// save / load via the Trecs Player window — see
    /// <see cref="TrailHistorySerializer"/>.
    ///
    /// <see cref="SharedPtr{T}"/> (and the writable blob store it requires)
    /// is a more advanced topic and is intentionally NOT used here — a
    /// follow-up sample can introduce sharing a heap object across entities.
    /// </summary>
    public class PointersCompositionRoot : CompositionRootBase
    {
        public int FollowerCount = 5;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var world = new WorldBuilder()
                .SetSettings(new WorldSettings { RandomSeed = 42 })
                .AddTemplate(SampleTemplates.PatrolFollowerEntity.Template)
                .Build();

            // Register a custom serializer for our managed pointer payload.
            // The Trecs Player window discovers the registry via the world,
            // so snapshots taken through that window round-trip TrailHistory
            // correctly.
            world.SerializerRegistry.RegisterSerializer<TrailHistorySerializer>();

            var goManager = new RenderableGameObjectManager(world);

            world.AddSystems(
                new ISystem[] { new PatrolMovementSystem(), new PatrolPresenter(goManager) }
            );

            // Register cleanup handler BEFORE spawning, so it catches
            // all future removals (including world disposal).
            var followerCleanup = new PatrolFollowerCleanup(world);

            var sceneInitializer = new SceneInitializer(world, FollowerCount, goManager);

            initializables = new() { world.Initialize, sceneInitializer.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { followerCleanup.Dispose, goManager.Dispose, world.Dispose };
        }
    }
}
