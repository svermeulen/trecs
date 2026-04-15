namespace Trecs.Samples.SpawnAndDestroy
{
    public static class SampleTags
    {
        public struct Sphere : ITag { }
    }

    public static partial class SampleTemplates
    {
        public partial class SphereEntity : ITemplate, IHasTags<SampleTags.Sphere>
        {
            public Position Position = Position.Default;
            public Lifetime Lifetime;
            public GameObjectId GameObjectId;
        }
    }
}
