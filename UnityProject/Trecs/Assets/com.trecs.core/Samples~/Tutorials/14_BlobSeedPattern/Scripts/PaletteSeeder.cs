using UnityEngine;

namespace Trecs.Samples.BlobSeedPattern
{
    /// <summary>
    /// Seeds the world's shared heap with one <see cref="ColorPalette"/> per stable
    /// <see cref="BlobId"/> at startup. The seeder holds <see cref="SharedAnchor{T}"/>s as members so
    /// the palettes stay pinned for the lifetime of the seeder — without an anchor, the cache
    /// could evict them between init and the first entity spawn. Entities later acquire their
    /// own <see cref="SharedPtr{T}"/>s by stable BlobId; the seeder's pin and the entity-side
    /// refcount layer keep the blob alive independently.
    /// <para>
    /// <see cref="SharedAnchor{T}"/> is the lower-level pinning handle: it keeps blob bytes resident
    /// without participating in the ECS refcount that <see cref="SharedPtr{T}"/> adds on top.
    /// That's the right shape for an anchor that has to outlive "no entities reference this yet".
    /// It is acquired through a <see cref="WorldAccessor"/> like every other heap operation —
    /// the seeder makes its own <see cref="AccessorRole.Unrestricted"/> accessor because it is
    /// setup code, not a system.
    /// </para>
    /// </summary>
    public class PaletteSeeder
    {
        readonly World _world;
        WorldAccessor _accessor;
        SharedAnchor<ColorPalette> _warm;
        SharedAnchor<ColorPalette> _cool;

        public PaletteSeeder(World world)
        {
            _world = world;
        }

        public void Initialize()
        {
            _accessor = _world.CreateAccessor(AccessorRole.Unrestricted);

            // Register a builder for each palette under a caller-chosen BlobId, then acquire a
            // pinning handle so the bytes stay resident. Later SharedPtr.Acquire(world, stableId)
            // calls find the registered blob and hand out ECS-refcounted handles to it.
            SharedAnchor.Register(_accessor, PaletteIds.Warm, BuildWarm);
            SharedAnchor.Register(_accessor, PaletteIds.Cool, BuildCool);
            _warm = SharedAnchor.Acquire<ColorPalette>(_accessor, PaletteIds.Warm);
            _cool = SharedAnchor.Acquire<ColorPalette>(_accessor, PaletteIds.Cool);
        }

        public void Dispose()
        {
            _warm.Dispose(_accessor);
            _cool.Dispose(_accessor);
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
