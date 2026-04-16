using System;
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
        readonly Settings _settings;

        public StarvationSystem(Settings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
        [WrapAsJob]
        static void ExecuteImpl(
            ref UniformScale scale,
            ref ColorComponent color,
            EntityIndex entityIndex,
            in NativeWorldAccessor world,
            [PassThroughArgument] Settings settings
        )
        {
            scale.Value -= settings.ShrinkRate * world.DeltaTime;

            if (scale.Value <= settings.MinScale)
            {
                world.RemoveEntity(entityIndex);
                return;
            }

            // Color indicates starvation: cyan (healthy) → red-orange (starving)
            // HSV lerp produces vibrant intermediates (green, yellow, orange) instead of muddy RGB blending
            // Remap so full starving/healthy colors are reached before the extremes
            float healthRaw =
                (scale.Value - settings.MinScale) / (settings.HealthyScale - settings.MinScale);
            float health = math.saturate(healthRaw / settings.HealthyColorThreshold);
            color.Value = Color.Lerp(settings.StarvingColor, settings.HealthyColor, health);
        }

        public void Execute()
        {
            ExecuteImpl(_settings);
        }

        [Serializable]
        public struct Settings
        {
            public float ShrinkRate;
            public float MinScale;
            public float HealthyScale;
            public Color HealthyColor;
            public Color StarvingColor;

            [Tooltip(
                "Health fraction (0-1) at which fish reaches full healthy color. E.g. 0.7 means full cyan at 70% health."
            )]
            public float HealthyColorThreshold;
        }
    }
}
