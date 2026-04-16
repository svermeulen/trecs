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
            public Position Position = default;
            public Lifetime Lifetime;
            public GameObjectId GameObjectId;
        }
    }
}
