using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.BlobSeedPattern
{
    public static class SampleTags
    {
        public struct Swatch : ITag { }
    }

    /// <summary>
    /// Stable, hand-authored <see cref="BlobId"/>s for the seeded palettes.
    /// Using explicit IDs (rather than letting the heap auto-mint them) means
    /// these assets keep the same identity regardless of init-time call order
    /// — the content-pipeline pattern described in
    /// docs/advanced/heap-allocation-rules.md.
    /// </summary>
    public static class PaletteIds
    {
        public static readonly BlobId Warm = new(1001);
        public static readonly BlobId Cool = new(1002);
    }

    /// <summary>
    /// A shared, immutable asset: an ordered list of colours. Lives on the
    /// managed heap because the backing array cannot be stored in an
    /// unmanaged struct component. One palette is shared by many entities via
    /// <see cref="SharedPtr{T}"/>.
    ///
    /// <para>Marked <c>[Immutable]</c> to satisfy the TRECS125 analyzer:
    /// shared managed blobs must not be mutated post-Alloc, since the
    /// BlobCache is not snapshotted with game state. The colour array is
    /// held as a private <c>readonly</c> field and exposed only as a
    /// <see cref="IReadOnlyList{T}"/> so external callers cannot reassign
    /// entries.</para>
    ///
    /// <para>The constructor takes ownership of the <c>Color[]</c>. Do not
    /// keep an alias to the array after passing it in — the analyzer cannot
    /// detect caller-held aliases, and mutation through one would corrupt
    /// the cached blob's logical state.</para>
    /// </summary>
    [Immutable]
    public sealed class ColorPalette
    {
        public readonly string Name;
        readonly Color[] _colors;

        public ColorPalette(string name, Color[] colors)
        {
            Name = name;
            _colors = colors;
        }

        public IReadOnlyList<Color> Colors => _colors;
    }

    /// <summary>
    /// References a shared palette in the world's shared heap. The
    /// <see cref="SharedPtr{T}"/> handle is a 12-byte value type stored inline
    /// in the component.
    /// </summary>
    public partial struct PaletteRef : IEntityComponent
    {
        public SharedPtr<ColorPalette> Value;
        public float CycleSpeed;
    }

    public static partial class SampleTemplates
    {
        public partial class SwatchEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SampleTags.Swatch>
        {
            Position Position;
            UniformScale Scale = new(1f);
            ColorComponent Color;
            PaletteRef Palette;
            PrefabId PrefabId = new(BlobSeedPatternPrefabs.Swatch);
        }
    }
}
