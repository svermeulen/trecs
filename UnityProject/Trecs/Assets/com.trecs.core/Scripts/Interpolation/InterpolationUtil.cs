using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Utility methods for the interpolation subsystem, including computing the
    /// normalized progress through the current fixed-update interval.
    /// </summary>
    public static class InterpolationUtil
    {
        static readonly TrecsLog _log = new(nameof(InterpolationUtil));

        public static float CalculatePercentThroughFixedFrame(WorldAccessor ecs)
        {
            var endOfFrameTime = ecs.VariableElapsedTime + ecs.VariableDeltaTime;

            var fixedCurrentTime = ecs.FixedElapsedTime;
            var fixedPreviousTime = fixedCurrentTime - ecs.FixedDeltaTime;

            if (fixedCurrentTime <= 0 || fixedPreviousTime < 0)
            {
                return 0f;
            }

            if (fixedCurrentTime <= fixedPreviousTime)
            {
                throw Assert.CreateException(
                    "Unexpected state when calculating percent through fixed frame. fixedCurrentTime: {}, fixedPreviousTime: {}",
                    fixedCurrentTime,
                    fixedPreviousTime
                );
            }

            Assert.That(fixedCurrentTime > 0);
            Assert.That(fixedPreviousTime >= 0f);
            Assert.That(fixedCurrentTime > fixedPreviousTime);

            if (fixedCurrentTime < endOfFrameTime)
            {
                // This warning is annoying since we often pause only fixed update
                // _log.WarningThrottled(
                //     1f, "Fixed update failed to catch up to variable update - interpolations will be incorrect");

                // Could extrapolate instead but many places assume value between 0 and 1
                // Also, extrapolation would break debugging logic where we pause only fixed update but continue
                // to let variable update run
                return 0.999f;
            }

            var percent =
                (endOfFrameTime - fixedPreviousTime) / (fixedCurrentTime - fixedPreviousTime);
            return percent;
        }
    }
}
