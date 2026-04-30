namespace Trecs.Samples.Aspects
{
    [ExecuteAfter(typeof(BoidMovementSystem))]
    public partial class BoidWrapSystem : ISystem
    {
        readonly float _halfSize;

        public BoidWrapSystem(float areaSize)
        {
            _halfSize = areaSize / 2f;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Boid boid)
        {
            ref var p = ref boid.Position;

            if (p.x > _halfSize)
            {
                p.x -= _halfSize * 2;
            }
            else if (p.x < -_halfSize)
            {
                p.x += _halfSize * 2;
            }

            if (p.z > _halfSize)
            {
                p.z -= _halfSize * 2;
            }
            else if (p.z < -_halfSize)
            {
                p.z += _halfSize * 2;
            }
        }

        partial struct Boid : IAspect, IWrite<Position> { }
    }
}
