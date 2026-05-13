using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.BlobStorage
{
    /// <summary>
    /// Spawns a 6×6 grid of entities, each referencing one of the two seeded
    /// palettes by stable <see cref="BlobId"/>. Many entities share a single
    /// blob — the palette's managed data lives once in the shared heap, and
    /// every entity holds just a 12-byte <see cref="SharedPtr{T}"/> handle.
    /// </summary>
    public class SceneInitializer
    {
        readonly World _world;
        readonly RenderableGameObjectManager _goManager;

        public SceneInitializer(World world, RenderableGameObjectManager goManager)
        {
            _world = world;
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(BlobStoragePrefabs.Swatch, CreateSwatch);

            var world = _world.CreateAccessor(AccessorRole.Fixed);
            const int gridSize = 6;
            const float spacing = 1.5f;
            float halfExtent = spacing * (gridSize - 1) * 0.5f;

            for (int x = 0; x < gridSize; x++)
            {
                for (int z = 0; z < gridSize; z++)
                {
                    bool useWarm = (x + z) % 2 == 0;
                    var paletteId = useWarm ? PaletteIds.Warm : PaletteIds.Cool;

                    world
                        .AddEntity<SampleTags.Swatch>()
                        .Set(
                            new Position(
                                new float3(x * spacing - halfExtent, 0f, z * spacing - halfExtent)
                            )
                        )
                        .Set(new UniformScale(1f))
                        .Set(new ColorComponent(Color.white))
                        .Set(
                            new PaletteRef
                            {
                                // AllocShared(stableId) looks up the blob the
                                // PaletteSeeder put under this ID and returns
                                // a fresh handle. Each entity holds its own
                                // handle so the blob lives until all entities
                                // (and the seeder) are disposed.
                                Value = world.Heap.AllocShared<ColorPalette>(paletteId),
                                CycleSpeed = useWarm ? 0.3f : 0.2f,
                            }
                        )
                        .AssertComplete();
                }
            }
        }

        static GameObject CreateSwatch()
        {
            return SampleUtil.CreatePrimitive(PrimitiveType.Cube);
        }
    }
}
