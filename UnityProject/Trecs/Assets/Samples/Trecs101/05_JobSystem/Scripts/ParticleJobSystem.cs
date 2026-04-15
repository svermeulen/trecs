using Unity.Burst;

namespace Trecs.Samples.JobSystem
{
    /// <summary>
    /// Moves particles and bounces them off walls using two chained
    /// Burst-compiled parallel jobs. Demonstrates IForEachComponentsJob
    /// and ScheduleParallel.
    /// </summary>
    public partial class ParticleJobSystem : ISystem
    {
        readonly float _halfSize;

        public ParticleJobSystem(float areaSize)
        {
            _halfSize = areaSize / 2f;
        }

        public void Execute()
        {
            // Schedule two jobs: move first, then bounce
            new MoveJob { DeltaTime = World.FixedDeltaTime }.ScheduleParallel(World);

            new BounceJob { HalfSize = _halfSize }.ScheduleParallel(World);
        }

        [BurstCompile]
        partial struct MoveJob
        {
            public float DeltaTime;

            [ForEachEntity(Tag = typeof(SampleTags.Particle))]
            public readonly void Execute(in Velocity velocity, ref Position position)
            {
                position.Value += DeltaTime * velocity.Value;
            }
        }

        [BurstCompile]
        partial struct BounceJob
        {
            public float HalfSize;

            [ForEachEntity(Tag = typeof(SampleTags.Particle))]
            public readonly void Execute(ref Velocity velocity, ref Position position)
            {
                if (position.Value.x > HalfSize || position.Value.x < -HalfSize)
                {
                    velocity.Value.x = -velocity.Value.x;
                }
                if (position.Value.y > HalfSize || position.Value.y < 0)
                {
                    velocity.Value.y = -velocity.Value.y;
                }
                if (position.Value.z > HalfSize || position.Value.z < -HalfSize)
                {
                    velocity.Value.z = -velocity.Value.z;
                }
            }
        }
    }
}
