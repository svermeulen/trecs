namespace Trecs.Samples.Aspects
{
    public partial class BoidMovementSystem : ISystem
    {
        // One way to iterate over aspects is via the generated Query method on the aspect type
        // Note that we could also have filtered using .WithTags<SampleTags.Boid>() instead of MatchByComponents
        public void Execute()
        {
            foreach (var boid in Boid.Query(World).MatchByComponents())
            {
                boid.Position += World.DeltaTime * boid.Speed * boid.Velocity;
            }
        }

        partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
    }
}
