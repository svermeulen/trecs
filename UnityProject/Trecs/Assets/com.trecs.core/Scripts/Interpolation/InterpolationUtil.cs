using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Utility methods for the interpolation subsystem, including computing the
    /// normalized progress through the current fixed-update interval.
    /// </summary>
    public static class InterpolationUtil
    {
        public static float CalculatePercentThroughFixedFrame(WorldAccessor world)
        {
            var fixedCurrentTime = world.FixedElapsedTime;
            var fixedPreviousTime = fixedCurrentTime - world.FixedDeltaTime;

            if (fixedCurrentTime <= 0 || fixedPreviousTime < 0)
            {
                return 0f;
            }

            var endOfFrameTime = world.VariableElapsedTime + world.VariableDeltaTime;

            if (fixedCurrentTime <= fixedPreviousTime)
            {
                throw TrecsDebugAssert.CreateException(
                    "Unexpected state when calculating percent through fixed frame. fixedCurrentTime: {0}, fixedPreviousTime: {1}",
                    fixedCurrentTime,
                    fixedPreviousTime
                );
            }

            TrecsDebugAssert.That(fixedCurrentTime > 0);
            TrecsDebugAssert.That(fixedPreviousTime >= 0f);
            TrecsDebugAssert.That(fixedCurrentTime > fixedPreviousTime);

            if (fixedCurrentTime < endOfFrameTime)
            {
                // Clamp instead of extrapolating: callers assume [0,1], and extrapolation
                // would break the debugging workflow of pausing only fixed update
                return 0.999f;
            }

            var percent =
                (endOfFrameTime - fixedPreviousTime) / (fixedCurrentTime - fixedPreviousTime);
            return percent;
        }
    }
}
