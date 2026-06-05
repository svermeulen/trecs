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

        // Disabled by the play mode smoke tests, where slow CI machines can't
        // run the benchmark at realtime and the catch-up warning would
        // otherwise fail the test via LogAssert.NoUnexpectedReceived
        public bool WarnOnFixedUpdateFallingBehind = true;
    }
}
