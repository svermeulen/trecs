using Unity.Burst;
using Unity.Mathematics;

namespace Trecs.Samples.Interpolation
{
    public static class SampleInterpolators
    {
        const string GroupName = "InterpolationSampleInterpolators";

        [GenerateInterpolatorSystem("PositionInterpolatedUpdater", GroupName)]
        [BurstCompile]
        public static void InterpolatePosition(
            in Position a,
            in Position b,
            ref Position result,
            float t
        )
        {
            result.Value = math.lerp(a.Value, b.Value, t);
        }

        [GenerateInterpolatorSystem("RotationInterpolatedUpdater", GroupName)]
        [BurstCompile]
        public static void InterpolateRotation(
            in Rotation a,
            in Rotation b,
            ref Rotation result,
            float t
        )
        {
            // nlerp is sufficient here — the angular delta between fixed frames
            // is small enough that the difference from slerp is imperceptible,
            // and nlerp is significantly cheaper.
            result.Value = math.nlerp(a.Value, b.Value, t);
        }
    }
}
