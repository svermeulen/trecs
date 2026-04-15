using Unity.Mathematics;

namespace Trecs.Samples.States
{
    /// <summary>
    /// Counts down the rest timer on Resting balls. When the timer expires,
    /// the ball is launched upward and transitions back to Active state.
    /// </summary>
    [ExecutesAfter(typeof(PhysicsSystem))]
    public partial class WakeUpSystem : ISystem
    {
        const float LaunchSpeed = 8f;

        [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
        void Execute(in RestingBall ball)
        {
            ball.RestTimer -= World.DeltaTime;

            if (ball.RestTimer <= 0)
            {
                // Launch in a random upward direction
                float angle = World.Rng.Next() * 2f * math.PI;
                ball.Velocity = new float3(math.cos(angle) * 2f, LaunchSpeed, math.sin(angle) * 2f);

                World.MoveTo<BallTags.Ball, BallTags.Active>(ball.EntityIndex);
            }
        }

        partial struct RestingBall : IAspect, IWrite<Velocity, RestTimer> { }
    }
}
