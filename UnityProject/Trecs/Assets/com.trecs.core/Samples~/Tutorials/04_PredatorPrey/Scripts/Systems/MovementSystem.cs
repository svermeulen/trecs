namespace Trecs.Samples.PredatorPrey
{
    public partial class MovementSystem : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Movable))]
        void Execute(in Mover mover)
        {
            mover.Position += World.DeltaTime * mover.Speed * mover.MoveDirection;
        }

        partial struct Mover : IAspect, IRead<MoveDirection, Speed>, IWrite<Position> { }
    }
}
