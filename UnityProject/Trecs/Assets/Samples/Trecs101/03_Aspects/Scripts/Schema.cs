namespace Trecs.Samples.Aspects
{
    public static class SampleTags
    {
        public struct Boid : ITag { }
    }

    public static partial class SampleTemplates
    {
        public partial class BoidEntity : ITemplate, IHasTags<SampleTags.Boid>
        {
            public Position Position = Position.Default;
            public Velocity Velocity;
            public Speed Speed;
            public GameObjectId GameObjectId;
        }
    }
}
