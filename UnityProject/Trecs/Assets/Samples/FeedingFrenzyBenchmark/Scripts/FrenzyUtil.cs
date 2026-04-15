using Unity.Mathematics;

namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public static class FrenzyUtil
    {
        public static float3 ChooseRandomMapPosition(
            float random1,
            float random2,
            float spread,
            float concentration,
            float y
        )
        {
            float angle = random1 * 2f * math.PI;
            float posRadius = spread * math.pow(-math.log(1f - random2), 1f / concentration);
            return new float3(math.cos(angle) * posRadius, y, math.sin(angle) * posRadius);
        }
    }
}
