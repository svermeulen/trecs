using UnityEngine;

namespace Trecs.Samples.HelloEntity
{
    public class SceneInitializer
    {
        readonly World _world;
        readonly GameObjectRegistry _gameObjectRegistry;

        public SceneInitializer(World world, GameObjectRegistry gameObjectRegistry)
        {
            _world = world;
            _gameObjectRegistry = gameObjectRegistry;
        }

        public void Initialize()
        {
            var world = _world.CreateAccessor();

            var cube = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "SpinnerCube";

            world
                .AddEntity<SampleTags.Spinner>()
                .Set(_gameObjectRegistry.Register(cube.gameObject));
        }
    }
}
