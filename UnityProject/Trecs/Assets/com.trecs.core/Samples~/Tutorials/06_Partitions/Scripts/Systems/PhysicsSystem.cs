using Unity.Mathematics;

namespace Trecs.Samples.Partitions
{
    /// <summary>
    /// Applies gravity, velocity integration, and floor bounce to Active balls.
    ///
    /// Because Active balls live in their own group (via IPartitionedBy), this loop
    /// iterates contiguous memory — no branches to skip resting balls.
    /// </summary>
    public partial class PhysicsSystem : ISystem
    {
        const float Gravity = -9.81f;
        const float FloorY = 0.5f;
        const float Bounciness = 0.7f;
        const float RestThreshold = 0.3f;

        [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
        void Execute(in ActiveBall ball)
        {
            var vel = ball.Velocity;
            vel.y += Gravity * World.DeltaTime;
            ball.Position += vel * World.DeltaTime;
            ball.Velocity = vel;

            if (ball.Position.y < FloorY)
            {
                var pos = ball.Position;
                pos.y = FloorY;
                ball.Position = pos;

                vel.y = -vel.y * Bounciness;
                vel.xz *= 0.98f; // friction
                ball.Velocity = vel;
            }

            // SetTag transitions the entity to a different partition — this moves
            // component data to a new contiguous array so iteration stays cache-friendly
            if (
                math.lengthsq(ball.Velocity) < RestThreshold * RestThreshold
                && ball.Position.y <= FloorY + 0.01f
            )
            {
                ball.Velocity = float3.zero;
                ball.RestTimer = 2f + World.Rng.Next() * 3f; // rest 2-5 seconds

                ball.UnsetTag<BallTags.Active>(World);
            }
        }

        partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
    }
}
