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
        public partial class SampleGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            SpawnSystem.State SpawnState = default;
        }

        public partial class CritterEntity
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SampleTags.Critter>
        {
            Position Position = default;
            Lifetime Lifetime;
            PrefabId PrefabId = new(MultipleWorldsPrefabs.Critter);
        }
    }
}
