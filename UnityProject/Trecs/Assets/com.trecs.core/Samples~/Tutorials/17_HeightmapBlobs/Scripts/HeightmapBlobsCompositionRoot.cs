// Companion docs: https://svermeulen.github.io/trecs/samples/18-heightmap-blobs/

using System;
using System.Collections.Generic;
using Trecs.Serialization;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Four flavors of "shared blob, looked up by a content-derived BlobId,
    /// then referenced from many entities":
    ///
    /// <list type="bullet">
    /// <item><see cref="HeightmapFlavor.ManagedSharedPtr"/> — managed
    ///   class <see cref="HeightmapData"/> behind a
    ///   <see cref="SharedPtr{T}"/>, sampled from a regular main-thread
    ///   <see cref="ISystem"/>.</item>
    /// <item><see cref="HeightmapFlavor.NativeSharedPtrInline"/> — unmanaged
    ///   struct <see cref="NativeHeightmapData"/> behind a
    ///   <see cref="NativeSharedPtr{T}"/>, sampled inside a Burst-compiled
    ///   <c>[WrapAsJob]</c> static method that resolves the handle via
    ///   <see cref="NativeWorldAccessor.SharedPtrResolver"/>. Heights live
    ///   inline in a <c>FixedArray256{float}</c>; seeded by
    ///   <c>NativeSharedPtr.Alloc(in value)</c> with the by-value copy
    ///   that implies.</item>
    /// <item><see cref="HeightmapFlavor.NativeSharedPtrTakingOwnership"/> —
    ///   header struct <see cref="NativeHeightmapDataLarge"/> behind a
    ///   <see cref="NativeSharedPtr{T}"/>, with heights living in the
    ///   trailing region of the same allocation. Seeded by
    ///   <c>NativeSharedPtr.AllocTakingOwnership</c>: the seed site builds
    ///   directly into the heap slot, with no intermediate copies and no
    ///   inline-storage cap.</item>
    /// <item><see cref="HeightmapFlavor.ManagedSharedPtrInterface"/> —
    ///   <see cref="MutableHeightmapData"/> behind a
    ///   <see cref="SharedPtr{T}"/> parameterised on the
    ///   <c>[Immutable]</c> <see cref="IReadOnlyHeightmapData"/> interface.
    ///   The concrete keeps public mutable fields and is populated via an
    ///   object initializer; entity-side callers only see the read-only
    ///   interface face.</item>
    /// </list>
    ///
    /// All four paths derive the heightmap's <see cref="BlobId"/> from the
    /// same <see cref="HeightmapDescriptor"/> (resolution, world size, max
    /// height, seed, frequency) using <see cref="UniqueHashGenerator"/> — so the same
    /// recipe always lands the same id, the cache hit is automatic, and
    /// the expensive heightmap build only runs on cold start.
    /// </summary>
    public class HeightmapBlobsCompositionRoot : CompositionRootBase
    {
        // Test hook — when non-null, replaces Settings.Flavor for the
        // duration of one Construct() call so SampleScenesPlayModeTests
        // can exercise both flavors without serializing extra scene
        // copies. Same pattern as DynamicCollections.
        public static HeightmapFlavor? FlavorOverride;

        public SampleSettings Settings = new();

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var flavor = FlavorOverride ?? Settings.Flavor;
            Settings.Flavor = flavor;

            // Register the BlitSerializer for HeightmapDescriptor *before*
            // we hash it via UniqueHashGenerator. UniqueHashGenerator uses
            // the SerializerRegistry to turn the typed value into bytes;
            // unmanaged value types like this just need a one-line blit
            // registration.
            var worldBuilder = new WorldBuilder().RegisterSerializer(
                new BlitSerializer<HeightmapDescriptor>()
            );

            switch (flavor)
            {
                case HeightmapFlavor.ManagedSharedPtr:
                    worldBuilder.AddTemplate(SampleTemplates.ManagedCharacter.Template);
                    break;
                case HeightmapFlavor.NativeSharedPtrInline:
                    worldBuilder.AddTemplate(SampleTemplates.NativeCharacter.Template);
                    break;
                case HeightmapFlavor.NativeSharedPtrTakingOwnership:
                    worldBuilder.AddTemplate(SampleTemplates.NativeCharacterLarge.Template);
                    break;
                case HeightmapFlavor.ManagedSharedPtrInterface:
                    worldBuilder.AddTemplate(SampleTemplates.InterfaceCharacter.Template);
                    break;
            }

            var world = worldBuilder.Build();
            var goManager = new RenderableGameObjectManager(world);

            var systems = new List<ISystem> { new CharacterMover(Settings) };
            switch (flavor)
            {
                case HeightmapFlavor.ManagedSharedPtr:
                    systems.Add(new ManagedHeightmapFollower());
                    break;
                case HeightmapFlavor.NativeSharedPtrInline:
                    systems.Add(new NativeHeightmapFollower());
                    break;
                case HeightmapFlavor.NativeSharedPtrTakingOwnership:
                    systems.Add(new NativeHeightmapFollowerLarge());
                    break;
                case HeightmapFlavor.ManagedSharedPtrInterface:
                    systems.Add(new InterfaceHeightmapFollower());
                    break;
            }
            systems.Add(new CharacterPresenter(goManager));
            world.AddSystems(systems);

            var sceneInitializer = new SceneInitializer(world, Settings, goManager);

            // Order: world.Initialize first, then the scene initializer
            // (which allocates the blob and spawns entities that reference
            // it). Symmetric dispose order: scene initializer, then go
            // manager, then world.
            initializables = new() { world.Initialize, sceneInitializer.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { sceneInitializer.Dispose, goManager.Dispose, world.Dispose };
        }
    }
}
