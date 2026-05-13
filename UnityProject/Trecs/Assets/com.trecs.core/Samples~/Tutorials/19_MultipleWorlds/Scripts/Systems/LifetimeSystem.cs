namespace Trecs.Samples.MultipleWorlds
{
    public partial class LifetimeSystem : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Critter))]
        void Execute(ref Lifetime lifetime, EntityAccessor entity)
        {
            lifetime.Value -= World.DeltaTime;

            if (lifetime.Value <= 0)
            {
                // The RenderableGameObjectManager pools the GameObject when
                // it observes OnRemoved on this entity.
                entity.Remove();
            }
        }
    }
}
