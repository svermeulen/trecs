using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Internal
{
    public static class MathUtil
    {
        public const float DefaultEpsilon = 0.0001f;

        public static bool Approximately(float left, float right, float epsilon = DefaultEpsilon)
        {
            return Mathf.Abs(left - right) <= epsilon;
        }

        public static bool Approximately(float2 left, float2 right, float epsilon = DefaultEpsilon)
        {
            return Approximately(left.x, right.x, epsilon)
                && Approximately(left.y, right.y, epsilon);
        }

        public static float Remap(
            float value,
            float fromMin,
            float fromMax,
            float toMin,
            float toMax
        )
        {
            float normalizedValue = (value - fromMin) / (fromMax - fromMin);
            return toMin + normalizedValue * (toMax - toMin);
        }

        /// <summary>
        /// Returns angle in the range [-180, 180] degrees
        /// </summary>
        public static float NormalizeAngleDegrees(float angle)
        {
            if (angle >= -180f && angle <= 180f)
                return angle;

            angle = ((angle % 360f) + 360f) % 360f;

            if (angle > 180f)
                angle -= 360f;

            return angle;
        }

        public static float RemapAndClamp(
            float value,
            float fromMin,
            float fromMax,
            float toMin,
            float toMax
        )
        {
            var lowOut = math.min(toMin, toMax);
            var highOut = math.max(toMin, toMax);

            return math.clamp(Remap(value, fromMin, fromMax, toMin, toMax), lowOut, highOut);
        }

        public static bool Approximately(
            Vector3 left,
            Vector3 right,
            float epsilon = DefaultEpsilon
        )
        {
            return Approximately(left.x, right.x, epsilon)
                && Approximately(left.y, right.y, epsilon)
                && Approximately(left.z, right.z, epsilon);
        }

        public static bool HasNan(Vector3 value)
        {
            return float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z);
        }
    }
}
