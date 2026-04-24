namespace Trecs.Samples.DataDrivenTemplates
{
    // Tags and components known at compile time. The data file composes
    // templates by picking names from these registries (see ArchetypeLoader).
    public static class SampleTags
    {
        public struct Spinner : ITag { }

        public struct Orbiter : ITag { }

        public struct Bobber : ITag { }
    }

    public partial struct OrbitParams : IEntityComponent
    {
        public float Radius;
        public float Speed;
        public float Phase;
    }

    public partial struct BobParams : IEntityComponent
    {
        public float Amplitude;
        public float Speed;
    }

    // Note: no [ITemplate] classes here. All templates in this sample are
    // constructed at runtime by ArchetypeLoader from a ScriptableObject.
}
