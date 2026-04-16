using System;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public enum FrenzyStateApproach
    {
        Branching,
        Sets,
        States,
    }

    public enum IterationStyle
    {
        ForEachMethodAspect,
        ForEachMethodComponents,
        AspectQuery,
        QueryGroupSlices,
        RawComponentBuffersJob,
        ForEachMethodAspectJob,
        ForEachMethodComponentsJob,
        WrapAsJobAspect,
        WrapAsJobComponents,
    }

    [Serializable]
    public class FrenzyConfigSettings
    {
        public FrenzyStateApproach StateApproach;
        public IterationStyle IterationStyle;
        public bool Deterministic;
    }
}
