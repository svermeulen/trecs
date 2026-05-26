using UnityEngine;

namespace Trecs.Samples.BlobSeedPattern
{
    /// <summary>
    /// Seeds the world's <see cref="BlobCache"/> with one <see cref="ColorPalette"/>
    /// per stable <see cref="BlobId"/> at startup. The seeder holds
    /// <see cref="BlobPtr{T}"/>s as members so the palettes stay pinned for the
    /// lifetime of the seeder — without an anchor, the cache could evict them
    /// between init and the first entity spawn. Entities later acquire their
    /// own <see cref="SharedPtr{T}"/>s by stable BlobId; the seeder's pin and
    /// the entity-side refcount layer keep the blob alive independently.
    /// </summary>
    public class PaletteSeeder
    {
        readonly BlobCache _blobCache;
        BlobPtr<ColorPalette> _warm;
        BlobPtr<ColorPalette> _cool;

        public PaletteSeeder(BlobCache blobCache)
        {
            _blobCache = blobCache;
        }

        public void Initialize()
        {
            // BlobPtr.Alloc(cache, stableId, blob) seeds the blob under a caller-chosen
            // BlobId and returns a pinning handle. Later SharedPtr.Acquire(heap, stableId)
            // calls find the seeded blob and hand out ECS-refcounted handles to it.
            _warm = BlobPtr.Alloc(_blobCache, PaletteIds.Warm, BuildWarm());
            _cool = BlobPtr.Alloc(_blobCache, PaletteIds.Cool, BuildCool());
        }

        public void Dispose()
        {
            _warm.Dispose(_blobCache);
            _cool.Dispose(_blobCache);
        }

        static ColorPalette BuildWarm() =>
            new(
                "Warm",
                new[]
                {
                    new Color(1.00f, 0.35f, 0.10f),
                    new Color(1.00f, 0.65f, 0.15f),
                    new Color(1.00f, 0.90f, 0.25f),
                    new Color(0.95f, 0.45f, 0.20f),
                }
            );

        static ColorPalette BuildCool() =>
            new(
                "Cool",
                new[]
                {
                    new Color(0.15f, 0.45f, 0.95f),
                    new Color(0.20f, 0.85f, 0.75f),
                    new Color(0.55f, 0.35f, 0.90f),
                    new Color(0.10f, 0.70f, 0.55f),
                }
            );
    }
}
