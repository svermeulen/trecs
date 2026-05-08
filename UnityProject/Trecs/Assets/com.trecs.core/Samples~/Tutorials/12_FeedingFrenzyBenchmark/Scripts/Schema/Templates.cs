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
            [Input(MissingInputBehavior.RetainCurrent)]
            DesiredPreset DesiredPreset = default;

            DesiredFishCount DesiredFishCount = default;
            DesiredMealCount DesiredMealCount = default;

            [VariableUpdateOnly]
            PerformanceStats PerformanceStats = default;

            [VariableUpdateOnly]
            PerfStatsIntermediate PerfStatsIntermediate = default;

            FrenzyConfig FrenzyConfig = default;
        }

        public partial class FishEntity
            : ITemplate,
                IExtends<CommonTemplates.Renderable>,
                ITagged<FrenzyTags.Fish>
        {
            Position Position;
            Rotation Rotation = new(quaternion.identity);
            Velocity Velocity = default;
            TargetMeal TargetMeal = default;
            Speed Speed;
            DestinationPosition DestinationPosition = default;
        }

        // Partitions variant - adds partition tags for group-based partition tracking
        public partial class PartitionsFishEntity
            : ITemplate,
                IExtends<FishEntity>,
                IHasPartition<FrenzyTags.NotEating>,
                IHasPartition<FrenzyTags.Eating> { }

        // Base meal entity (no eating partition tracking)
        public partial class MealEntity
            : ITemplate,
                IExtends<CommonTemplates.Renderable>,
                ITagged<FrenzyTags.Meal>
        {
            Rotation Rotation = new(quaternion.identity);
            Position Position;
            ApproachingFish ApproachingFish = default;
        }

        // Partitions meal variant - adds partition tags
        public partial class PartitionsMealEntity
            : ITemplate,
                IExtends<MealEntity>,
                IHasPartition<FrenzyTags.NotEating>,
                IHasPartition<FrenzyTags.Eating> { }
    }
}
