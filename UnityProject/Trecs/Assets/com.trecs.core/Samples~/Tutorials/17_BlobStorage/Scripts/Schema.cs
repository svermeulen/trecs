using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.BlobStorage
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
    /// managed heap because <see cref="List{T}"/> cannot be stored in an
    /// unmanaged struct component. One palette is shared by many entities via
    /// <see cref="SharedPtr{T}"/>.
    /// </summary>
    public class ColorPalette
    {
        public string Name;
        public List<Color> Colors;
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
        public partial class SwatchEntity : ITemplate, IHasTags<SampleTags.Swatch>
        {
            public Position Position;
            public UniformScale Scale = new(1f);
            public ColorComponent Color;
            public PaletteRef Palette;
            public GameObjectId GameObjectId;
        }
    }
}
