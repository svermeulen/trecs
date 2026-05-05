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
        public partial class SphereEntity : ITemplate, IHasTags<SampleTags.Sphere>
        {
            Position Position = default;
            Lifetime Lifetime;
            GameObjectId GameObjectId;
        }
    }
}
