using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzy101
{
    // ─── Tags ───────────────────────────────────────────────────────
    public static class FrenzyTags
    {
        public struct Fish : ITag { }

        public struct Meal : ITag { }

        // States — fish and meals transition between these
        public struct NotEating : ITag { }

        public struct Eating : ITag { }
    }

    // ─── Components ─────────────────────────────────────────────────

    [Unwrap]
    public partial struct Velocity : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct Speed : IEntityComponent
    {
        public float Value;
    }

    /// <summary>
    /// Stable handle to the meal entity this fish is approaching.
    /// </summary>
    [Unwrap]
    public partial struct TargetMeal : IEntityComponent
    {
        public EntityHandle Value;
    }

    /// <summary>
    /// World-space position the fish is moving toward.
    /// </summary>
    [Unwrap]
    public partial struct DestinationPosition : IEntityComponent
    {
        public float3 Value;
    }

    /// <summary>
    /// Nutritional value of a meal. When consumed, the fish grows by
    /// this amount. Randomly assigned at spawn time (0.5–1.5).
    /// </summary>
    [Unwrap]
    public partial struct MealNutrition : IEntityComponent
    {
        public float Value;
    }

    /// <summary>
    /// Simulation position — the "true" position updated by fixed-update
    /// systems. Position (the rendered position) chases this smoothly
    /// in the variable-update VisualSmoothingSystem.
    /// </summary>
    [Unwrap]
    public partial struct SimPosition : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct ApproachingFish : IEntityComponent
    {
        public EntityHandle Value;
    }

    /// <summary>
    /// Simulation rotation — the "true" rotation. Rotation chases this.
    /// </summary>
    [Unwrap]
    public partial struct SimRotation : IEntityComponent
    {
        public quaternion Value;
    }

    [Unwrap]
    public partial struct DesiredFishCount : IEntityComponent
    {
        public int Value;
    }

    [Unwrap]
    public partial struct DesiredMealCount : IEntityComponent
    {
        public int Value;
    }

    // ─── Templates ──────────────────────────────────────────────────
    public static partial class SampleTemplates
    {
        /// <summary>
        /// Fish entity with two states: NotEating (idle, bobbing) and
        /// Eating (moving toward a meal). States create separate memory
        /// groups for cache-friendly iteration.
        /// </summary>
        public partial class FishEntity
            : ITemplate,
                IExtends<CommonTemplates.Renderable>,
                IHasTags<FrenzyTags.Fish>,
                IHasState<FrenzyTags.NotEating>,
                IHasState<FrenzyTags.Eating>
        {
            public Rotation Rotation = new(quaternion.identity);
            public SimPosition SimPosition = default;
            public SimRotation SimRotation = new() { Value = quaternion.identity };
            public Velocity Velocity = default;
            public Speed Speed;
            public TargetMeal TargetMeal = default;
            public DestinationPosition DestinationPosition = default;
        }

        /// <summary>
        /// Meal entity with two states matching the fish states.
        /// When a fish targets a meal, both move to the Eating state.
        /// </summary>
        public partial class MealEntity
            : ITemplate,
                IExtends<CommonTemplates.Renderable>,
                IHasTags<FrenzyTags.Meal>,
                IHasState<FrenzyTags.NotEating>,
                IHasState<FrenzyTags.Eating>
        {
            public Rotation Rotation = new(quaternion.identity);
            public MealNutrition Nutrition;
            public ApproachingFish ApproachingFish = default;
        }

        /// <summary>
        /// Global entity storing desired entity counts, controlled
        /// by keyboard input at runtime.
        /// </summary>
        public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            public DesiredFishCount DesiredFishCount = new() { Value = 1000 };
            public DesiredMealCount DesiredMealCount = default;
        }
    }
}
