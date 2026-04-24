using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.BlobStorage
{
    public static class SampleTags
    {
        public struct Swatch : ITag { }
    }

    /// <summary>
    /// A large-ish shared asset: an ordered list of colours. Lives on the managed
    /// heap because <see cref="List{T}"/> cannot be stored in an unmanaged struct
    /// component. One palette is shared by many entities via <see cref="BlobPtr{T}"/>.
    /// </summary>
    public class ColorPalette
    {
        public string Name;
        public List<Color> Colors;
    }

    /// <summary>
    /// References a palette stored in the <see cref="BlobCache"/>. The entity's
    /// colour cycles through the palette over time.
    /// </summary>
    public partial struct PaletteRef : IEntityComponent
    {
        public BlobPtr<ColorPalette> Value;
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
