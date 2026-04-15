namespace Trecs.Samples.PredatorPrey
{
    public static class SampleTags
    {
        public struct Predator : ITag { }

        public struct Prey : ITag { }
    }

    public static partial class SampleTemplates
    {
        public partial class PredatorEntity : ITemplate, IHasTags<SampleTags.Predator>
        {
            public Position Position = Position.Default;
            public Velocity Velocity;
            public Speed Speed;
            public GameObjectId GameObjectId;
        }

        public partial class PreyEntity : ITemplate, IHasTags<SampleTags.Prey>
        {
            public Position Position = Position.Default;
            public GameObjectId GameObjectId;
        }
    }
}
