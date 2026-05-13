namespace Trecs.Samples.HelloEntity
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;

        public SceneInitializer(World world)
        {
            _world = world.CreateAccessor(AccessorRole.Unrestricted);
        }

        public void Initialize()
        {
            _world.AddEntity<SampleTags.Spinner>();
        }
    }
}
