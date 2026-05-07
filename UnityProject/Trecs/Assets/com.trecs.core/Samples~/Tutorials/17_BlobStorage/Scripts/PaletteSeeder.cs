using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.BlobStorage
{
    /// <summary>
    /// Seeds the world's shared heap with one <see cref="ColorPalette"/> per
    /// stable <see cref="BlobId"/> at startup. The seeder holds the
    /// <see cref="SharedPtr{T}"/>s as members so the palettes stay alive even
    /// when no entities reference them — without an anchor, the refcount would
    /// drop to zero between init and the first entity spawn and the blob would
    /// be evicted.
    /// </summary>
    public class PaletteSeeder
    {
        readonly World _world;
        SharedPtr<ColorPalette> _warm;
        SharedPtr<ColorPalette> _cool;

        public PaletteSeeder(World world)
        {
            _world = world;
        }

        public void Initialize()
        {
            var world = _world.CreateAccessor(AccessorRole.Unrestricted);

            // AllocShared(stableId, blob) seeds the blob under a caller-chosen
            // BlobId. Subsequent AllocShared(stableId) calls (without data)
            // look up the seeded blob and return new handles to it.
            _warm = world.Heap.AllocShared(PaletteIds.Warm, BuildWarm());
            _cool = world.Heap.AllocShared(PaletteIds.Cool, BuildCool());
        }

        public void Dispose()
        {
            var world = _world.CreateAccessor(AccessorRole.Unrestricted);
            _warm.Dispose(world.Heap);
            _cool.Dispose(world.Heap);
        }

        static ColorPalette BuildWarm() =>
            new()
            {
                Name = "Warm",
                Colors = new List<Color>
                {
                    new(1.00f, 0.35f, 0.10f),
                    new(1.00f, 0.65f, 0.15f),
                    new(1.00f, 0.90f, 0.25f),
                    new(0.95f, 0.45f, 0.20f),
                },
            };

        static ColorPalette BuildCool() =>
            new()
            {
                Name = "Cool",
                Colors = new List<Color>
                {
                    new(0.15f, 0.45f, 0.95f),
                    new(0.20f, 0.85f, 0.75f),
                    new(0.55f, 0.35f, 0.90f),
                    new(0.10f, 0.70f, 0.55f),
                },
            };
    }
}
