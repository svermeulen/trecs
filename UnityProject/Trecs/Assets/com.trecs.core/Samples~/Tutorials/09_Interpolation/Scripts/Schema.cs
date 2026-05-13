namespace Trecs.Samples.Interpolation
{
    public static class OrbitTags
    {
        public struct Smooth : ITag { }

        public struct Raw : ITag { }
    }

    public partial struct OrbitParams : IEntityComponent
    {
        public float Radius;
        public float Speed;
        public float Phase;
        public float CenterX;
    }

    public static partial class SampleTemplates
    {
        /// <summary>
        /// Interpolated entity: [Interpolated] generates Interpolated and
        /// InterpolatedPrevious wrapper components. The interpolation system
        /// blends between fixed-frame snapshots for smooth variable-rate rendering.
        /// </summary>
        public partial class SmoothOrbitEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<OrbitTags.Smooth>
        {
            [Interpolated]
            Position Position = default;

            [Interpolated]
            Rotation Rotation = default;
            OrbitParams OrbitParams;
            PrefabId PrefabId = new(InterpolationPrefabs.SmoothCube);
        }

        /// <summary>
        /// Non-interpolated entity: Position is updated in fixed update only.
        /// The renderer reads Position directly, which may appear jittery
        /// when fixed and variable update rates differ.
        /// </summary>
        public partial class RawOrbitEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<OrbitTags.Raw>
        {
            Position Position = default;
            Rotation Rotation = default;
            OrbitParams OrbitParams;
            PrefabId PrefabId = new(InterpolationPrefabs.RawCube);
        }
    }
}
