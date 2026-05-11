using Unity.Mathematics;

namespace Trecs.Samples.Partitions
{
    /// <summary>
    /// Counts down the rest timer on Resting balls. When the timer expires,
    /// the ball is launched upward and transitions back to the Active partition.
    /// </summary>
    [ExecuteAfter(typeof(PhysicsSystem))]
    public partial class WakeUpSystem : ISystem
    {
        const float LaunchSpeed = 8f;

        [ForEachEntity(typeof(BallTags.Ball), Without = typeof(BallTags.Active))]
        void Execute(in RestingBall ball)
        {
            ball.RestTimer -= World.DeltaTime;

            if (ball.RestTimer <= 0)
            {
                float angle = World.Rng.Next() * 2f * math.PI;
                ball.Velocity = new float3(math.cos(angle) * 2f, LaunchSpeed, math.sin(angle) * 2f);

                ball.SetTag<BallTags.Active>(World);
            }
        }

        partial struct RestingBall : IAspect, IWrite<Velocity, RestTimer> { }
    }
}
