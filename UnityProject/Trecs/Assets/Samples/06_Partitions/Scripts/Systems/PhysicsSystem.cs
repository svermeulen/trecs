using Unity.Mathematics;

namespace Trecs.Samples.Partitions
{
    /// <summary>
    /// Applies gravity, velocity integration, and floor bounce to Active balls.
    ///
    /// Because Active balls live in their own group (via IHasPartition), this loop
    /// iterates contiguous memory — no branches to skip resting balls.
    /// </summary>
    public partial class PhysicsSystem : ISystem
    {
        const float Gravity = -9.81f;
        const float FloorY = 0.5f;
        const float Bounciness = 0.7f;
        const float RestThreshold = 0.3f;

        [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
        void Execute(in ActiveBall ball)
        {
            // Gravity + integration
            var vel = ball.Velocity;
            vel.y += Gravity * World.DeltaTime;
            ball.Position += vel * World.DeltaTime;
            ball.Velocity = vel;

            // Floor bounce
            if (ball.Position.y < FloorY)
            {
                var pos = ball.Position;
                pos.y = FloorY;
                ball.Position = pos;

                vel.y = -vel.y * Bounciness;
                vel.xz *= 0.98f; // friction
                ball.Velocity = vel;
            }

            // Transition to Resting when energy is low
            if (
                math.lengthsq(ball.Velocity) < RestThreshold * RestThreshold
                && ball.Position.y <= FloorY + 0.01f
            )
            {
                ball.Velocity = float3.zero;
                ball.RestTimer = 2f + World.Rng.Next() * 3f; // rest 2-5 seconds

                ball.MoveTo<BallTags.Ball, BallTags.Resting>(World);
            }
        }

        partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
    }
}
