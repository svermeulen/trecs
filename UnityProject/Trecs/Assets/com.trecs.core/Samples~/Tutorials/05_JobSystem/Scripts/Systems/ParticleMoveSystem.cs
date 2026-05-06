namespace Trecs.Samples.JobSystem
{
    /// <summary>
    /// Same logic implemented two ways: main-thread and [WrapAsJob].
    /// The job version must be static and use NativeWorldAccessor instead of World.
    /// The source generator creates the Burst-compiled job struct automatically.
    /// </summary>
    public partial class ParticleMoveSystem : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Particle))]
        void ExecuteMainThread(in Velocity velocity, ref Position position)
        {
            position.Value += World.DeltaTime * velocity.Value;
        }

        [ForEachEntity(typeof(SampleTags.Particle))]
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
