namespace Trecs.Samples.Aspects
{
    public partial class BoidMovementSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Boid boid)
        {
            boid.Position += World.FixedDeltaTime * boid.Speed * boid.Velocity;
        }

        partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
    }
}
