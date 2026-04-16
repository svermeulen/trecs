using Unity.Mathematics;

namespace Trecs.Samples.PredatorPrey
{
    public static class SampleTags
    {
        public struct Predator : ITag { }

        public struct Prey : ITag { }

        public struct Movable : ITag { }
    }

    [Unwrap]
    public partial struct ApproachingPredator : IEntityComponent
    {
        public EntityHandle Value;
    }

    [Unwrap]
    public partial struct ChosenPrey : IEntityComponent
    {
        public EntityHandle Value;
    }

    [Unwrap]
    public partial struct MoveDirection : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct Speed : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class Movable : ITemplate, IHasTags<SampleTags.Movable>
        {
            public Position Position = default;
            public MoveDirection MoveDirection = default;
            public Speed Speed;
            public GameObjectId GameObjectId;
        }

        public partial class PredatorEntity
            : ITemplate,
                IHasTags<SampleTags.Predator>,
                IExtends<Movable>
        {
            public ChosenPrey ChosenPrey = default;
        }

        public partial class PreyEntity : ITemplate, IHasTags<SampleTags.Prey>, IExtends<Movable>
        {
            public ApproachingPredator ApproachingPredator = default;
        }
    }
}
