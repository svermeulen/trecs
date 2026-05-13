namespace Trecs.Samples.SpawnAndDestroy
{
    public static class SampleTags
    {
        public struct Sphere : ITag { }
    }

    [Unwrap]
    public partial struct Lifetime : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class SphereEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SampleTags.Sphere>
        {
            Position Position = default;
            Lifetime Lifetime;
            ColorComponent Color = new(UnityEngine.Color.white);
            PrefabId PrefabId = new(SpawnAndDestroyPrefabs.Sphere);
        }
    }
}
