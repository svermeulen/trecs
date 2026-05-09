namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    [ExecuteBefore(typeof(IManageFishCount))]
    public partial class FrenzyConfigUpdateSystem : ISystem
    {
        public void Execute()
        {
            var desired = World.GlobalComponent<DesiredIterationStyle>().Read.Value;
            ref var config = ref World.GlobalComponent<FrenzyConfig>().Write;
            config.IterationStyle = desired;
        }
    }
}
