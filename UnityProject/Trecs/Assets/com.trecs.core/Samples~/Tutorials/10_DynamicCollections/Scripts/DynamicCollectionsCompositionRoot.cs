// Companion docs: https://svermeulen.github.io/trecs/samples/10-dynamic-collections/

using System;
using System.Collections.Generic;
using Trecs.Serialization;
using Unity.Mathematics;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Five ways to attach a dynamic per-entity collection to a Trecs
    /// component, side by side:
    ///
    /// <list type="bullet">
    /// <item><see cref="TrailCollectionType.UniquePtrQueue"/> — managed
    ///   <c>Queue&lt;Vector3&gt;</c> behind a <see cref="UniquePtr{T}"/>,
    ///   trimmed to <c>SampleSettings.TrailLength</c> every frame.</item>
    /// <item><see cref="TrailCollectionType.FixedArrayRingBuffer"/> — inline
    ///   <see cref="Trecs.Collections.FixedArray32{T}"/> ring buffer, blittable.</item>
    /// <item><see cref="TrailCollectionType.FixedListAppend"/> — inline
    ///   <see cref="Trecs.Collections.FixedList256{T}"/> appended until full.</item>
    /// <item><see cref="TrailCollectionType.TrecsListAppend"/> — heap-backed
    ///   <see cref="TrecsList{T}"/> that grows geometrically without bound.</item>
    /// <item><see cref="TrailCollectionType.TrecsArrayRingBuffer"/> — heap-backed
    ///   <see cref="TrecsArray{T}"/> used as a ring buffer; length chosen at
    ///   allocation time (<c>SampleSettings.TrailLength</c>) and fixed thereafter.</item>
    /// </list>
    ///
    /// Pick the variant via the <c>CollectionType</c> field on the scene
    /// inspector. The composition root spawns one Character template
    /// variant and registers one trail-updater + presenter pair to match.
    /// </summary>
    public class DynamicCollectionsCompositionRoot : CompositionRootBase
    {
        // Test hook — when non-null, replaces Settings.CollectionType for the
        // duration of one Construct() call so SampleScenesPlayModeTests can
        // exercise every variant without serializing extra scene copies.
        public static TrailCollectionType? CollectionTypeOverride;

        public SampleSettings Settings = new();

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var collectionType = CollectionTypeOverride ?? Settings.CollectionType;
            CollectionTypeOverride = null;
            Settings.CollectionType = collectionType;

            var worldBuilder = new WorldBuilder()
            // Unlike unmanaged data, for managed data we need a custom
            // serializer in order to use it in a UniquePtr
            // (unmanaged data can be directly blitted)
            .RegisterSerializer<QueueSerializer<float3>>();

            switch (collectionType)
            {
                case TrailCollectionType.UniquePtrQueue:
                    worldBuilder.AddTemplate(SampleTemplates.CharacterQueue.Template);
                    break;
                case TrailCollectionType.FixedArrayRingBuffer:
                    worldBuilder.AddTemplate(SampleTemplates.CharacterFixedArray.Template);
                    break;
                case TrailCollectionType.FixedListAppend:
                    worldBuilder.AddTemplate(SampleTemplates.CharacterFixedList.Template);
                    break;
                case TrailCollectionType.TrecsListAppend:
                    worldBuilder.AddTemplate(SampleTemplates.CharacterTrecsList.Template);
                    break;
                case TrailCollectionType.TrecsArrayRingBuffer:
                    worldBuilder.AddTemplate(SampleTemplates.CharacterTrecsArray.Template);
                    break;
            }

            var world = worldBuilder.Build();
            var goManager = new RenderableGameObjectManager(world);

            var systems = new List<ISystem> { new CharacterMover(Settings) };
            switch (collectionType)
            {
                case TrailCollectionType.UniquePtrQueue:
                    systems.Add(new QueueTrailUpdater(Settings));
                    systems.Add(new QueueTrailPresenter(goManager));
                    break;
                case TrailCollectionType.FixedArrayRingBuffer:
                    systems.Add(new FixedArrayTrailUpdater(Settings));
                    systems.Add(new FixedArrayTrailPresenter(goManager));
                    break;
                case TrailCollectionType.FixedListAppend:
                    systems.Add(new FixedListTrailUpdater(Settings));
                    systems.Add(new FixedListTrailPresenter(goManager));
                    break;
                case TrailCollectionType.TrecsListAppend:
                    systems.Add(new TrecsListTrailUpdater(Settings));
                    systems.Add(new TrecsListTrailPresenter(goManager));
                    break;
                case TrailCollectionType.TrecsArrayRingBuffer:
                    systems.Add(new TrecsArrayTrailUpdater(Settings));
                    systems.Add(new TrecsArrayTrailPresenter(goManager));
                    break;
            }
            world.AddSystems(systems);

            var sceneLifecycle = new SceneLifecycle(world, Settings, goManager);

            initializables = new() { world.Initialize, sceneLifecycle.Initialize };

            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { goManager.Dispose, world.Dispose };
        }
    }
}
