namespace Trecs.Samples.JobSystem
{
    public partial class ParticleMoveSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(SampleTags.Particle))]
        void ExecuteMainThread(in Velocity velocity, ref Position position)
        {
            position.Value += World.DeltaTime * velocity.Value;
        }

        [ForEachEntity(Tag = typeof(SampleTags.Particle))]
        [WrapAsJob]
        static void ExecuteAsJob(
            in Velocity velocity,
            ref Position position,
            in NativeWorldAccessor world
        )
        {
            position.Value += world.DeltaTime * velocity.Value;
        }

        public void Execute()
        {
            if (World.GlobalComponent<IsJobsEnabled>().Read.Value)
            {
                ExecuteAsJob();
            }
            else
            {
                ExecuteMainThread();
            }
        }
    }
}
