using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Sets
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly SampleSettings _settings;

        public SceneInitializer(
            World world,
            GameObjectRegistry gameObjectRegistry,
            SampleSettings settings
        )
        {
            _world = world.CreateAccessor();
            _gameObjectRegistry = gameObjectRegistry;
            _settings = settings;
        }

        public void Initialize()
        {
            var offset = (_settings.GridSize - 1) * _settings.Spacing * 0.5f;

            for (int x = 0; x < _settings.GridSize; x++)
            {
                for (int z = 0; z < _settings.GridSize; z++)
                {
                    var position = new float3(
                        x * _settings.Spacing - offset,
                        0.5f,
                        z * _settings.Spacing - offset
                    );

                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = $"Particle_{x}_{z}";
                    go.transform.position = (Vector3)position;
                    go.transform.localScale = Vector3.one * _settings.BaseScale;
                    go.GetComponent<Renderer>().material.color = Color.gray;

                    _world
                        .AddEntity<SampleTags.Particle>()
                        .Set(new Position(position))
                        .Set(_gameObjectRegistry.Register(go))
                        .AssertComplete();
                }
            }
        }
    }
}
