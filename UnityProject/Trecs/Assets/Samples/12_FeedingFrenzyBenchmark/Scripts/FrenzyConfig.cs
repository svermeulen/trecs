using System;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public enum FrenzySubsetApproach
    {
        Branching,
        Sets,
        Partitions,
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
        public FrenzySubsetApproach SubsetApproach;
        public IterationStyle IterationStyle;
        public bool Deterministic;
    }
}
