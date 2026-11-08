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
            if (world.FixedFrame == 0)
            {
                return 0f;
            }

            var offset = world.InterpolationOffset;

            // Clamp instead of extrapolating: callers assume [0,1], and extrapolation
            // would break the debugging workflow of pausing only fixed update
            if (offset >= 0)
            {
                return 0.999f;
            }

            return 1f + offset / world.FixedDeltaTime;
        }
    }
}
