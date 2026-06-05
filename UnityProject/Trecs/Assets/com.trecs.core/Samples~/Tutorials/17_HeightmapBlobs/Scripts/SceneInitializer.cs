using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Registers a per-descriptor-type heightmap builder on the shared-pointer
    /// type (the descriptor-taking <c>SharedAnchor.Register</c> /
    /// <c>NativeSharedAnchor.Register</c> overloads), then
    /// acquires a handle straight from the heightmap descriptor — which hashes it
    /// to a content-derived <see cref="BlobId"/> and dedups against the cache —
    /// and spawns characters that each hold their own handle to the same blob. The
    /// shared blob lives once on the heap regardless of how many characters reference it.
    /// </summary>
    public partial class SceneInitializer : IDisposable
    {
        readonly World _world;
        readonly SampleSettings _settings;
        readonly RenderableGameObjectManager _goManager;
        readonly DisposeCollection _subscriptions = new();

        // Anchor handles — held for the lifetime of the scene so the cache
        // doesn't evict the blob between init and the first frame. SharedAnchor /
        // NativeSharedAnchor are the dedicated pinning type for non-ECS holders
        // like this seeder: they keep the blob resident without taking the
        // entity-side refcount that the per-character SharedPtr handles do.
        // Matches the seeder pattern in Sample 14 (Blob Seed Pattern).
        SharedAnchor<HeightmapData> _managedAnchor;
        NativeSharedAnchor<NativeHeightmapData> _nativeAnchor;
        NativeSharedAnchor<NativeHeightmapDataLarge> _nativeLargeAnchor;
        SharedAnchor<IReadOnlyHeightmapData> _interfaceAnchor;

        // Visual representation of the surface, owned by the scene rather
        // than an entity since it's a one-off.
        GameObject _surface;

        WorldAccessor _accessor;

        public SceneInitializer(
            World world,
            SampleSettings settings,
            RenderableGameObjectManager goManager
        )
        {
            _world = world;
            _settings = settings;
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(HeightmapBlobsPrefabs.Character, CreateCharacter);

            var world = _world.CreateAccessor(AccessorRole.Unrestricted);
            _accessor = world;

            var descriptor = new HeightmapDescriptor
            {
                Resolution = _settings.HeightmapResolution,
                WorldSize = _settings.HeightmapWorldSize,
                MaxHeight = _settings.HeightmapMaxHeight,
                Seed = _settings.HeightmapSeed,
                Frequency = _settings.HeightmapFrequency,
            };

            // Each flavor registers its builder once with the interner, then interns the
            // descriptor: the interner hashes it to a content-derived BlobId, dedups against the
            // cache, runs the builder only on a miss, and hands back a pinning anchor handle.
            switch (_settings.Flavor)
            {
                case HeightmapFlavor.ManagedSharedPtr:
                    InitializeManaged(world, descriptor);
                    break;
                case HeightmapFlavor.NativeSharedPtrInline:
                    InitializeNative(world, descriptor);
                    break;
                case HeightmapFlavor.NativeSharedPtrTakingOwnership:
                    InitializeNativeLarge(world, descriptor);
                    break;
                case HeightmapFlavor.ManagedSharedPtrInterface:
                    InitializeInterface(world, descriptor);
                    break;
            }

            _surface = HeightmapBuilder.CreateSurfaceVisual(descriptor);
            SpawnCharacters(world);

            switch (_settings.Flavor)
            {
                case HeightmapFlavor.ManagedSharedPtr:
                    world
                        .Events.EntitiesWithTags<SampleTags.ManagedFollower>()
                        .OnRemoved(OnManagedRemoved)
                        .AddTo(_subscriptions);
                    break;
                case HeightmapFlavor.NativeSharedPtrInline:
                    world
                        .Events.EntitiesWithTags<SampleTags.NativeFollower>()
                        .OnRemoved(OnNativeRemoved)
                        .AddTo(_subscriptions);
                    break;
                case HeightmapFlavor.NativeSharedPtrTakingOwnership:
                    world
                        .Events.EntitiesWithTags<SampleTags.NativeFollowerLarge>()
                        .OnRemoved(OnNativeLargeRemoved)
                        .AddTo(_subscriptions);
                    break;
                case HeightmapFlavor.ManagedSharedPtrInterface:
                    world
                        .Events.EntitiesWithTags<SampleTags.InterfaceFollower>()
                        .OnRemoved(OnInterfaceRemoved)
                        .AddTo(_subscriptions);
                    break;
            }

            world.Events.OnShutdown(() => _subscriptions.Dispose()).AddTo(_subscriptions);
        }

        void InitializeManaged(WorldAccessor world, HeightmapDescriptor descriptor)
        {
            // Register the per-descriptor-type builder once, then acquire straight from the
            // descriptor. The builder runs lazily on first access and again if the cache ever evicts
            // and re-materializes the blob — so it must rebuild the same data purely from its
            // descriptor argument (the lambda captures nothing, so it's a cached static delegate —
            // no per-call alloc).
            SharedAnchor.Register<HeightmapDescriptor, HeightmapData>(
                world,
                d => new HeightmapData(d, HeightmapBuilder.BuildManagedHeights(d))
            );
            _managedAnchor = SharedAnchor.Acquire<HeightmapDescriptor, HeightmapData>(
                world,
                descriptor
            );
        }

        void InitializeNative(WorldAccessor world, HeightmapDescriptor descriptor)
        {
            // NativeSharedPtr is for unmanaged blobs and lets Burst jobs resolve the data via
            // NativeSharedPtrResolver. Same intern / dedup / re-derive logic as the managed path.
            //
            // The blob is a NativeHeightmapData struct that fits inline (FixedArray256<float>), so
            // the inline-value builder shape copies the bytes into a freshly-allocated heap slot —
            // no taking-ownership needed. For heightmaps larger than 256 cells, switch to
            // the taking-ownership Register overload (see the large flavor below). Note this
            // approach copies the whole inline struct on each (re)materialization, so it scales
            // poorly.
            NativeSharedAnchor.Register<HeightmapDescriptor, NativeHeightmapData>(
                world,
                d => HeightmapBuilder.BuildNativeHeightsInline(d)
            );
            _nativeAnchor = NativeSharedAnchor.Acquire<HeightmapDescriptor, NativeHeightmapData>(
                world,
                descriptor
            );
        }

        void InitializeInterface(WorldAccessor world, HeightmapDescriptor descriptor)
        {
            // Same managed builder shape as InitializeManaged, but the blob type is the
            // [Immutable] IReadOnlyHeightmapData interface and the builder returns a mutable
            // MutableHeightmapData populated via an object initializer. The heap holds the concrete
            // cast to the interface; entity-side reads only see the read-only face.
            SharedAnchor.Register<HeightmapDescriptor, IReadOnlyHeightmapData>(
                world,
                d => new MutableHeightmapData
                {
                    Descriptor = d,
                    Heights = HeightmapBuilder.BuildManagedHeights(d),
                }
            );
            _interfaceAnchor = SharedAnchor.Acquire<HeightmapDescriptor, IReadOnlyHeightmapData>(
                world,
                descriptor
            );
        }

        void InitializeNativeLarge(WorldAccessor world, HeightmapDescriptor descriptor)
        {
            // The taking-ownership builder shape: BlobBuilder lays out the root struct + heights in
            // a single contiguous allocation, patches the BlobArray<float>'s offset, and returns a
            // NativeBlobAllocation the cache takes ownership of. Heights are filled directly into
            // the builder's reserved region — no intermediate stack copy and no inline-storage cap.
            // Routing it through the interner (rather than an eager BlobBuilder.Build) means the
            // blob re-derives from its descriptor after an eviction, just like the other flavors.
            NativeSharedAnchor.Register<HeightmapDescriptor, NativeHeightmapDataLarge>(
                world,
                BuildLargeHeightmapAllocation
            );
            _nativeLargeAnchor = NativeSharedAnchor.Acquire<
                HeightmapDescriptor,
                NativeHeightmapDataLarge
            >(world, descriptor);
        }

        static NativeBlobAllocation BuildLargeHeightmapAllocation(HeightmapDescriptor descriptor)
        {
            var cells = descriptor.Resolution * descriptor.Resolution;

            using var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<NativeHeightmapDataLarge>();
            root.Descriptor = descriptor;

            var heights = builder.Allocate(in root.Heights, cells);
            for (int z = 0; z < descriptor.Resolution; z++)
            {
                for (int x = 0; x < descriptor.Resolution; x++)
                {
                    heights[z * descriptor.Resolution + x] = HeightmapBuilder.SampleNoise(
                        x,
                        z,
                        descriptor
                    );
                }
            }

            return builder.BuildNativeBlobAllocation();
        }

        void SpawnCharacters(WorldAccessor world)
        {
            float half = _settings.HeightmapWorldSize * 0.5f;
            for (int i = 0; i < _settings.CharacterCount; i++)
            {
                // Spread the wander seeds widely so characters sample
                // uncorrelated regions of the noise field rather than
                // tracing the same path.
                float offset = i * 37.13f;
                var initial = new float3(
                    math.lerp(-half, +half, (i + 0.5f) / _settings.CharacterCount),
                    0f,
                    0f
                );

                switch (_settings.Flavor)
                {
                    case HeightmapFlavor.ManagedSharedPtr:
                        SpawnManaged(world, offset, initial);
                        break;
                    case HeightmapFlavor.NativeSharedPtrInline:
                        SpawnNative(world, offset, initial);
                        break;
                    case HeightmapFlavor.NativeSharedPtrTakingOwnership:
                        SpawnNativeLarge(world, offset, initial);
                        break;
                    case HeightmapFlavor.ManagedSharedPtrInterface:
                        SpawnInterface(world, offset, initial);
                        break;
                }
            }
        }

        void SpawnManaged(WorldAccessor world, float offset, float3 initial)
        {
            // The seeder's anchor pins the blob; each entity acquires its own
            // ECS-refcounted SharedPtr to it by id. The anchor and the entity
            // handles keep the same underlying blob alive independently.
            world
                .AddEntity<SampleTags.Character, SampleTags.ManagedFollower>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(
                    new ManagedHeightmapRef(
                        SharedPtr.Acquire<HeightmapData>(world, _managedAnchor.BlobId)
                    )
                )
                .AssertComplete();
        }

        void SpawnNative(WorldAccessor world, float offset, float3 initial)
        {
            world
                .AddEntity<SampleTags.Character, SampleTags.NativeFollower>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(
                    new NativeHeightmapRef(
                        NativeSharedPtr.Acquire<NativeHeightmapData>(world, _nativeAnchor.BlobId)
                    )
                )
                .AssertComplete();
        }

        void SpawnNativeLarge(WorldAccessor world, float offset, float3 initial)
        {
            world
                .AddEntity<SampleTags.Character, SampleTags.NativeFollowerLarge>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(
                    new NativeHeightmapRefLarge(
                        NativeSharedPtr.Acquire<NativeHeightmapDataLarge>(
                            world,
                            _nativeLargeAnchor.BlobId
                        )
                    )
                )
                .AssertComplete();
        }

        void SpawnInterface(WorldAccessor world, float offset, float3 initial)
        {
            world
                .AddEntity<SampleTags.Character, SampleTags.InterfaceFollower>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(
                    new InterfaceHeightmapRef(
                        SharedPtr.Acquire<IReadOnlyHeightmapData>(world, _interfaceAnchor.BlobId)
                    )
                )
                .AssertComplete();
        }

        [ForEachEntity]
        void OnManagedRemoved(in ManagedHeightmapRef heightmapRef)
        {
            heightmapRef.Value.Dispose(_accessor);
        }

        [ForEachEntity]
        void OnNativeRemoved(in NativeHeightmapRef heightmapRef)
        {
            heightmapRef.Value.Dispose(_accessor);
        }

        [ForEachEntity]
        void OnNativeLargeRemoved(in NativeHeightmapRefLarge heightmapRef)
        {
            heightmapRef.Value.Dispose(_accessor);
        }

        [ForEachEntity]
        void OnInterfaceRemoved(in InterfaceHeightmapRef heightmapRef)
        {
            heightmapRef.Value.Dispose(_accessor);
        }

        GameObject CreateCharacter()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * _settings.CharacterSize;
            go.GetComponent<Renderer>().material.color = new Color(0.9f, 0.5f, 0.2f);
            return go;
        }

        public void Dispose()
        {
            // Drop the seeder's anchor handle. Entity-owned handles aren't
            // explicitly disposed here (this sample doesn't remove entities
            // during play); they're released by the framework when the
            // world tears down. For samples that do remove entities mid-
            // run, hook an OnRemoved observer and dispose each entity's
            // handle there — see docs/advanced/shared-heap-data.md.
            var world = _world.CreateAccessor(AccessorRole.Unrestricted);
            if (!_managedAnchor.IsNull)
            {
                _managedAnchor.Dispose(world);
            }
            if (!_nativeAnchor.IsNull)
            {
                _nativeAnchor.Dispose(world);
            }
            if (!_nativeLargeAnchor.IsNull)
            {
                _nativeLargeAnchor.Dispose(world);
            }
            if (!_interfaceAnchor.IsNull)
            {
                _interfaceAnchor.Dispose(world);
            }

            if (_surface != null)
            {
                Object.Destroy(_surface);
            }
        }
    }
}
