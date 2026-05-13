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
        // Base template — PredatorEntity and PreyEntity inherit these components via IExtends
        public abstract partial class Movable
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SampleTags.Movable>
        {
            Position Position = default;
            MoveDirection MoveDirection = default;
            Speed Speed;
        }

        public partial class PredatorEntity
            : ITemplate,
                ITagged<SampleTags.Predator>,
                IExtends<Movable>
        {
            ChosenPrey ChosenPrey = default;
            PrefabId PrefabId = new(PredatorPreyPrefabs.Predator);
        }

        public partial class PreyEntity : ITemplate, ITagged<SampleTags.Prey>, IExtends<Movable>
        {
            ApproachingPredator ApproachingPredator = default;
            PrefabId PrefabId = new(PredatorPreyPrefabs.Prey);
        }
    }
}
