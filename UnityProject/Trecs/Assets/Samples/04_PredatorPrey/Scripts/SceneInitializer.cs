using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
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
            var rng = _world.FixedRng;

            for (int i = 0; i < _settings.PredatorCount; i++)
            {
                var position = new float3(
                    rng.NextFloat(-_settings.SpawnRadius, _settings.SpawnRadius),
                    0.5f,
                    rng.NextFloat(-_settings.SpawnRadius, _settings.SpawnRadius)
                );

                var go = SampleUtil.CreatePrimitive(PrimitiveType.Capsule);
                go.name = $"Predator_{i}";
                go.transform.localScale = new Vector3(0.6f, 0.6f, 1.2f);
                go.transform.position = (Vector3)position;
                go.GetComponent<Renderer>().material.color = Color.red;

                _world
                    .AddEntity<SampleTags.Predator>()
                    .Set(new Position(position))
                    .Set(new Speed(_settings.PredatorSpeed))
                    .Set(_gameObjectRegistry.Register(go));
            }
        }
    }
}
