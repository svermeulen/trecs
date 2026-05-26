namespace Trecs.Samples.ReactiveEvents
{
    public partial class BubblePhysicsSystem : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Bubble))]
        void Execute(ref Position position, in Velocity velocity)
        {
            position.Value += velocity.Value * World.DeltaTime;
        }
    }
}
