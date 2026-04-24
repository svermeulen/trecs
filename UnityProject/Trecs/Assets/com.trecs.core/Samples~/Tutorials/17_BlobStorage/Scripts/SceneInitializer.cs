using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.BlobStorage
{
    /// <summary>
    /// Creates two palettes as blobs, then spawns a grid of entities each
    /// referencing one of them via <see cref="BlobPtr{T}"/>. Many entities
    /// share a single blob — the blob's managed data (the colour list) is
    /// stored once in the <see cref="BlobCache"/>, and every entity holds just
    /// a 16-byte handle.
    /// </summary>
    public class SceneInitializer
    {
        readonly World _world;
        readonly BlobCache _blobCache;
        readonly GameObjectRegistry _registry;

        public SceneInitializer(World world, BlobCache blobCache, GameObjectRegistry registry)
        {
            _world = world;
            _blobCache = blobCache;
            _registry = registry;
        }

        public void Initialize()
        {
            // Create blobs once. CreateBlobPtr takes ownership: the blob lives
            // in the writable BlobStore registered on the WorldBuilder, and its
            // lifetime is managed by the cache. We do NOT hold the raw object
            // afterwards — entities reference it via BlobPtr.
            var warm = _blobCache.CreateBlobPtr(
                new ColorPalette
                {
                    Name = "Warm",
                    Colors = new List<Color>
                    {
                        new(1.00f, 0.35f, 0.10f),
                        new(1.00f, 0.65f, 0.15f),
                        new(1.00f, 0.90f, 0.25f),
                        new(0.95f, 0.45f, 0.20f),
                    },
                }
            );

            var cool = _blobCache.CreateBlobPtr(
                new ColorPalette
                {
                    Name = "Cool",
                    Colors = new List<Color>
                    {
                        new(0.15f, 0.45f, 0.95f),
                        new(0.20f, 0.85f, 0.75f),
                        new(0.55f, 0.35f, 0.90f),
                        new(0.10f, 0.70f, 0.55f),
                    },
                }
            );

            var world = _world.CreateAccessor();
            const int gridSize = 6;
            const float spacing = 1.5f;
            float halfExtent = spacing * (gridSize - 1) * 0.5f;

            for (int x = 0; x < gridSize; x++)
            {
                for (int z = 0; z < gridSize; z++)
                {
                    var position = new float3(
                        x * spacing - halfExtent,
                        0f,
                        z * spacing - halfExtent
                    );

                    // Alternate which palette each entity references. Both
                    // palettes are stored once in the cache no matter how many
                    // entities point at each.
                    bool useWarm = (x + z) % 2 == 0;
                    var paletteRef = useWarm ? warm : cool;

                    var go = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
                    go.name = useWarm ? "Warm" : "Cool";

                    world
                        .AddEntity<SampleTags.Swatch>()
                        .Set(new Position(position))
                        .Set(new UniformScale(1f))
                        .Set(new ColorComponent(Color.white))
                        .Set(
                            new PaletteRef
                            {
                                // Clone bumps the handle refcount so the blob
                                // stays alive while this entity references it.
                                Value = paletteRef.Clone(_blobCache),
                                CycleSpeed = useWarm ? 0.3f : 0.2f,
                            }
                        )
                        .Set(_registry.Register(go))
                        .AssertComplete();
                }
            }

            // Dispose our original handles. The cloned handles on the entities
            // keep the blobs alive; when the entities are destroyed, those
            // handles are released and the blobs become eligible for eviction.
            warm.Dispose(_blobCache);
            cool.Dispose(_blobCache);
        }
    }
}
