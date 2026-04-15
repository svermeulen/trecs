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
        /// Base renderable template. Entities extending this are rendered
        /// by the shared RendererSystem via GPU indirect instancing.
        /// </summary>
        public partial class Renderable : ITemplate, IHasTags<CommonTags.Renderable>
        {
            public Position Position;
            public Rotation Rotation;
            public UniformScale Scale;
            public ColorComponent Color = ColorComponent.Default;
        }

        /// <summary>
        /// Fish entity with two states: NotEating (idle, bobbing) and
        /// Eating (moving toward a meal). States create separate memory
        /// groups for cache-friendly iteration.
        /// </summary>
        public partial class FishEntity
            : ITemplate,
                IExtends<Renderable>,
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
                IExtends<Renderable>,
                IHasTags<FrenzyTags.Meal>,
                IHasState<FrenzyTags.NotEating>,
                IHasState<FrenzyTags.Eating>
        {
            public Rotation Rotation = new(quaternion.identity);
            public MealNutrition Nutrition;
        }

        /// <summary>
        /// Global entity storing desired entity counts, controlled
        /// by keyboard input at runtime.
        /// </summary>
        public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
        {
            public DesiredFishCount DesiredFishCount = new() { Value = 100 };
            public DesiredMealCount DesiredMealCount = default;
        }
    }
}
