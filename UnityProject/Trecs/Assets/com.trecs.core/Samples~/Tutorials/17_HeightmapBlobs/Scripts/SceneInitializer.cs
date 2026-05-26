using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Trecs.Samples.HeightmapBlobs
{
    /// <summary>
    /// Creates the heightmap blob under a content-derived
    /// <see cref="BlobId"/>, anchors it with a member-held
    /// <c>SharedPtr</c> / <c>NativeSharedPtr</c>, then spawns characters
    /// that each hold their own handle to the same blob. The shared blob
    /// lives once on the heap regardless of how many characters reference it.
    /// </summary>
    public partial class SceneInitializer : IDisposable
    {
        readonly World _world;
        readonly SampleSettings _settings;
        readonly RenderableGameObjectManager _goManager;
        readonly UniqueHashGenerator _hashGenerator;
        readonly DisposeCollection _subscriptions = new();

        // Anchor handles — held for the lifetime of the scene so the cache
        // doesn't evict the blob between init and the first frame. Without
        // these, every character entity would still hold a handle, but
        // there's a brief window where refcount could drop to zero (e.g.
        // if all character spawns were deferred). Matches the seeder
        // pattern in Sample 15 / BlobSeedPattern.
        SharedPtr<HeightmapData> _managedAnchor;
        NativeSharedPtr<NativeHeightmapData> _nativeAnchor;
        NativeSharedPtr<NativeHeightmapDataLarge> _nativeLargeAnchor;
        SharedPtr<IReadOnlyHeightmapData> _interfaceAnchor;

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

            // UniqueHashGenerator needs the world's SerializerRegistry — it
            // serializes the descriptor with the registered BlitSerializer
            // and hashes the resulting bytes to derive a BlobId. The
            // composition root registered BlitSerializer<HeightmapDescriptor>
            // before we got here.
            _hashGenerator = new UniqueHashGenerator(world.SerializerRegistry);
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

            // The recipe → BlobId step. Hashing the descriptor (not the
            // built heights) is cheap and lets us probe the cache *before*
            // doing the expensive build. Same descriptor on a later run
            // hits the cache and skips BuildManagedHeights entirely.
            var blobId = new BlobId(_hashGenerator.Generate(descriptor));

            switch (_settings.Flavor)
            {
                case HeightmapFlavor.ManagedSharedPtr:
                    InitializeManaged(world, descriptor, blobId);
                    break;
                case HeightmapFlavor.NativeSharedPtrInline:
                    InitializeNative(world, descriptor, blobId);
                    break;
                case HeightmapFlavor.NativeSharedPtrTakingOwnership:
                    InitializeNativeLarge(world, descriptor, blobId);
                    break;
                case HeightmapFlavor.ManagedSharedPtrInterface:
                    InitializeInterface(world, descriptor, blobId);
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

        void InitializeManaged(WorldAccessor world, HeightmapDescriptor descriptor, BlobId blobId)
        {
            // GetOrAlloc: if a blob is already cached under this id, return
            // a fresh handle to it; otherwise call the factory and seed it.
            // The factory only runs on cache miss — same recipe ⇒ no
            // rebuild.
            // Note that for this example cache miss always happens since our blob store
            // is inmemory only
            _managedAnchor = SharedPtr.GetOrAlloc(
                world,
                blobId,
                () =>
                    new HeightmapData(descriptor, HeightmapBuilder.BuildManagedHeights(descriptor))
            );
        }

        void InitializeNative(WorldAccessor world, HeightmapDescriptor descriptor, BlobId blobId)
        {
            // NativeSharedPtr is for unmanaged blobs and lets Burst jobs
            // resolve the data via NativeSharedPtrResolver. Same cache-miss /
            // cache-hit logic as the managed path.
            //
            // The blob is a NativeHeightmapData struct that fits inline
            // (FixedArray256<float>), so NativeSharedPtr.Alloc on a value-
            // type blob copies the bytes into a freshly-allocated heap slot
            // — no AllocTakingOwnership needed. For heightmaps larger than
            // 256 cells, you'd switch to AllocTakingOwnership with a
            // separately-allocated NativeBlobAllocation.
            //
            // Note that for this sample, cache miss always happens since our blob store
            // is inmemory only
            if (!NativeSharedPtr.TryGet(world, blobId, out _nativeAnchor))
            {
                // Note here that this approach requires several large copies
                // so would work less well as size gets bigger
                var blob = HeightmapBuilder.BuildNativeHeightsInline(descriptor);
                _nativeAnchor = NativeSharedPtr.Alloc(world, blobId, in blob);
            }
        }

        void InitializeInterface(WorldAccessor world, HeightmapDescriptor descriptor, BlobId blobId)
        {
            // Same cache-miss-only factory shape as InitializeManaged, but
            // SharedPtr.GetOrAlloc<T> is called with T = IReadOnlyHeightmapData
            // (the [Immutable] interface) and the factory returns a
            // mutable MutableHeightmapData populated via an object
            // initializer. The heap holds the concrete cast to the
            // interface; entity-side reads only see the read-only face.
            // Note that for this sample, cache miss always happens since our blob store
            // is inmemory only
            if (!SharedPtr.TryGet<IReadOnlyHeightmapData>(world, blobId, out _interfaceAnchor))
            {
                var data = new MutableHeightmapData();

                data.Descriptor = descriptor;
                data.Heights = HeightmapBuilder.BuildManagedHeights(descriptor);

                _interfaceAnchor = SharedPtr.Alloc<IReadOnlyHeightmapData>(world, blobId, data);
            }
        }

        void InitializeNativeLarge(
            WorldAccessor world,
            HeightmapDescriptor descriptor,
            BlobId blobId
        )
        {
            // Same recipe → BlobId cache logic as the inline native flavor.
            // BlobBuilder lays out the root struct + heights in a single
            // contiguous allocation, patches the BlobArray<float>'s offset,
            // and hands the result to NativeSharedPtr.AllocTakingOwnership.
            // The heights are filled directly into the builder's reserved
            // region via BlobBuilderArray's indexer — no intermediate stack
            // copy and no inline-storage cap.
            // Note that for this sample, cache miss always happens since our blob store
            // is inmemory only
            if (NativeSharedPtr.TryGet(world, blobId, out _nativeLargeAnchor))
            {
                return;
            }

            var cells = descriptor.Resolution * descriptor.Resolution;

            using (var builder = new BlobBuilder(Allocator.Temp))
            {
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

                _nativeLargeAnchor = builder.Build<NativeHeightmapDataLarge>(world, blobId);
            }
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
            // Clone() bumps the refcount and returns a fresh handle for
            // this entity — both the anchor and the entity hold valid
            // handles to the same underlying blob.
            world
                .AddEntity<SampleTags.Character, SampleTags.ManagedFollower>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(new ManagedHeightmapRef(_managedAnchor.Clone(world)))
                .AssertComplete();
        }

        void SpawnNative(WorldAccessor world, float offset, float3 initial)
        {
            world
                .AddEntity<SampleTags.Character, SampleTags.NativeFollower>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(new NativeHeightmapRef(_nativeAnchor.Clone(world)))
                .AssertComplete();
        }

        void SpawnNativeLarge(WorldAccessor world, float offset, float3 initial)
        {
            world
                .AddEntity<SampleTags.Character, SampleTags.NativeFollowerLarge>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(new NativeHeightmapRefLarge(_nativeLargeAnchor.Clone(world)))
                .AssertComplete();
        }

        void SpawnInterface(WorldAccessor world, float offset, float3 initial)
        {
            world
                .AddEntity<SampleTags.Character, SampleTags.InterfaceFollower>()
                .Set(new Position(initial))
                .Set(new NoiseOffset(offset))
                .Set(new InterfaceHeightmapRef(_interfaceAnchor.Clone(world)))
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

            _hashGenerator.Dispose();

            if (_surface != null)
            {
                Object.Destroy(_surface);
            }
        }
    }
}
