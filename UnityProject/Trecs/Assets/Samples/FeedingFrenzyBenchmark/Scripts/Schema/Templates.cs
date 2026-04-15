using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    // For non-globals templates, providing a "= default;" value indicates to Trecs that an
    // initial value is optional (the caller does not need to provide one via the initializer).
    // For globals templates, all fields must have explicit defaults since the global entity
    // is created automatically by the system.
    public static partial class Templates
    {
        public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            [Input(MissingInputFrameBehaviour.RetainCurrent)]
            public DesiredPreset DesiredPreset = default;

            public DesiredFishCount DesiredFishCount = default;
            public DesiredMealCount DesiredMealCount = default;

            [VariableUpdateOnly]
            public PerformanceStats PerformanceStats = default;

            [VariableUpdateOnly]
            public PerfStatsIntermediate PerfStatsIntermediate = default;

            public FrenzyConfig FrenzyConfig = default;
        }

        public partial class Renderable : ITemplate, IHasTags<CommonTags.Renderable>
        {
            public Position Position;
            public Rotation Rotation;
            public UniformScale Scale;
            public ColorComponent Color = ColorComponent.Default;
        }

        public partial class FishEntity : ITemplate, IExtends<Renderable>, IHasTags<FrenzyTags.Fish>
        {
            public Position Position;
            public Rotation Rotation = new(quaternion.identity);
            public Velocity Velocity = default;
            public TargetMeal TargetMeal = default;
            public Speed Speed;
            public DestinationPosition DestinationPosition = default;
        }

        // States variant - adds state tags for group-based state tracking
        public partial class StatesFishEntity
            : ITemplate,
                IExtends<FishEntity>,
                IHasState<FrenzyTags.NotEating>,
                IHasState<FrenzyTags.Eating> { }

        // Base meal entity (no eating state tracking)
        public partial class MealEntity : ITemplate, IExtends<Renderable>, IHasTags<FrenzyTags.Meal>
        {
            public Rotation Rotation = new(quaternion.identity);
            public Position Position;
            public ApproachingFish ApproachingFish = default;
        }

        // States meal variant - adds state tags
        public partial class StatesMealEntity
            : ITemplate,
                IExtends<MealEntity>,
                IHasState<FrenzyTags.NotEating>,
                IHasState<FrenzyTags.Eating> { }
    }
}
