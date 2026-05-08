namespace Trecs.Samples.MultipleWorlds
{
    public static class SampleTags
    {
        public struct Critter : ITag { }
    }

    [Unwrap]
    public partial struct Lifetime : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class CritterEntity : ITemplate, ITagged<SampleTags.Critter>
        {
            Position Position = default;
            Lifetime Lifetime;
            GameObjectId GameObjectId;
        }
    }
}
