using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Tests
{
    public static class TestUtil
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
    }
}
