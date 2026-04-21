using System.Runtime.CompilerServices;

namespace Trecs.Samples.JobSystem
{
    public partial class ParticleBoundSystem : ISystem
    {
        readonly float _halfSize;

        public ParticleBoundSystem(float areaSize)
        {
            _halfSize = areaSize / 2f;
        }

        [ForEachEntity(Tag = typeof(SampleTags.Particle))]
        void ExecuteMainThread(ref Velocity velocity, ref Position position)
        {
            Bounce(ref velocity, ref position, _halfSize);
        }

        // Note that it is static in this case since it is called from inside a job
        // and that all parameters must be native compatible
        [ForEachEntity(Tag = typeof(SampleTags.Particle))]
        [WrapAsJob]
        static void ExecuteAsJob(
            ref Velocity velocity,
            ref Position position,
            [PassThroughArgument] float halfSize
        )
        {
            Bounce(ref velocity, ref position, halfSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Bounce(ref Velocity velocity, ref Position position, float halfSize)
        {
            if (position.Value.x > halfSize || position.Value.x < -halfSize)
            {
                velocity.Value.x = -velocity.Value.x;
            }
            if (position.Value.y > halfSize || position.Value.y < -halfSize)
            {
                velocity.Value.y = -velocity.Value.y;
            }
            if (position.Value.z > halfSize || position.Value.z < -halfSize)
            {
                velocity.Value.z = -velocity.Value.z;
            }
        }

        public void Execute()
        {
            if (World.GlobalComponent<IsJobsEnabled>().Read.Value)
            {
                ExecuteAsJob(_halfSize);
            }
            else
            {
                ExecuteMainThread();
            }
        }
    }
}
