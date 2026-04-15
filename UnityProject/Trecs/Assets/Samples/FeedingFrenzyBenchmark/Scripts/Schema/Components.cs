using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [Unwrap]
    public partial struct Velocity : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct TargetMeal : IEntityComponent
    {
        public EntityHandle Value;
    }

    [Unwrap]
    public partial struct ApproachingFish : IEntityComponent
    {
        public EntityHandle Value;
    }

    [Unwrap]
    public partial struct Speed : IEntityComponent
    {
        public float Value;
    }

    [Unwrap]
    public partial struct DestinationPosition : IEntityComponent
    {
        public float3 Value;
    }

    [Unwrap]
    public partial struct DesiredPreset : IEntityComponent
    {
        public int Value;
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

    public partial struct PerfStatsIntermediate : IEntityComponent
    {
        public float SimTickHzMin;
        public float SimTickHzMax;
        public float SimTickHzSum;
        public int SimTickHzSampleCount;
        public float FpsMin;
        public float FpsMax;
        public float FpsSum;
        public int FpsSampleCount;

        // Running sum of sim-tick durations (ms) that have occurred since the
        // last variable update; snapshotted+reset on OnVariableUpdateStarted.
        public float SimPerFrameMsAccum;
        public float SimPerFrameMsSum;
        public int SimPerFrameMsSampleCount;
        public float LastResetTime;
    }

    public partial struct PerformanceStats : IEntityComponent
    {
        public int SimTickHzAvg;
        public int SimTickHzMin;
        public int SimTickHzMax;
        public int FpsAvg;
        public int FpsMin;
        public int FpsMax;
        public int EntityCount;
        public int TotalMemMb;
        public int MonoMemMb;
        public float SimTickMs;
        public float SimPerFrameMs;
        public float FrameMs;
    }

    public partial struct FrenzyConfig : IEntityComponent
    {
        public FrenzyStateApproach StateApproach;
        public IterationStyle IterationStyle;
        public bool Deterministic;
    }
}
