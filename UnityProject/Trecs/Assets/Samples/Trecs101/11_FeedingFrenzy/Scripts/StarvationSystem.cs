using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.FeedingFrenzy101
{
    /// <summary>
    /// Shrinks all fish over time. When a fish becomes too small, it
    /// starves and is removed from the world. Also updates fish color
    /// to indicate starvation level (cyan = healthy, red-orange = starving).
    ///
    /// Demonstrates: [WrapAsJob] with a single-tag filter that matches
    /// multiple state groups (both Eating and NotEating fish), and
    /// entity removal via NativeWorldAccessor in a parallel job.
    /// </summary>
    public partial class StarvationSystem : ISystem
    {
        const float ShrinkRate = 0.02f;
        const float MinScale = 0.1f;
        const float HealthyScale = 1.5f;

        static readonly Color HealthyColor = Color.cyan;
        static readonly Color StarvingColor = new(1f, 0.2f, 0f);

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void Execute(
            ref UniformScale scale,
            ref ColorComponent color,
            EntityIndex entityIndex,
            in NativeWorldAccessor world
        )
        {
            scale.Value -= ShrinkRate * world.DeltaTime;

            if (scale.Value <= MinScale)
            {
                world.RemoveEntity(entityIndex);
                return;
            }

            // Color indicates starvation: cyan (healthy) → red-orange (starving)
            float health = math.saturate((scale.Value - MinScale) / (HealthyScale - MinScale));
            color.Value = Color.Lerp(StarvingColor, HealthyColor, health);
        }
    }
}
