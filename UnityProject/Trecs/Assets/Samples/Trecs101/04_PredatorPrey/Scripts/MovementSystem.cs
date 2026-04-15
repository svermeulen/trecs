namespace Trecs.Samples.PredatorPrey
{
    [ExecutesAfter(typeof(ChaseSystem))]
    public partial class MovementSystem : ISystem
    {
        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Mover mover)
        {
            mover.Position += World.FixedDeltaTime * mover.Speed * mover.Velocity;
        }

        partial struct Mover : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
    }
}
