namespace Trecs.Samples.SpawnAndDestroy
{
    public partial class LifetimeSystem : ISystem
    {
        [ForEachEntity(typeof(SampleTags.Sphere))]
        void Execute(ref Lifetime lifetime, EntityAccessor entity)
        {
            lifetime.Value -= World.DeltaTime;

            if (lifetime.Value <= 0)
            {
                entity.Remove();
            }
        }
    }
}
