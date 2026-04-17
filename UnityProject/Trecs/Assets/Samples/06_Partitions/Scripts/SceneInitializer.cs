using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Partitions
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly int _ballCount;
        readonly float _spawnRadius;

        public SceneInitializer(
            World world,
            GameObjectRegistry gameObjectRegistry,
            int ballCount,
            float spawnRadius
        )
        {
            _world = world.CreateAccessor();
            _gameObjectRegistry = gameObjectRegistry;
            _ballCount = ballCount;
            _spawnRadius = spawnRadius;
        }

        public void Initialize()
        {
            var rng = _world.FixedRng;

            for (int i = 0; i < _ballCount; i++)
            {
                var position = new float3(
                    rng.NextFloat(-_spawnRadius, _spawnRadius),
                    rng.NextFloat(3f, 15f),
                    rng.NextFloat(-_spawnRadius, _spawnRadius)
                );

                var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Ball_{i}";
                go.transform.position = (Vector3)position;
                go.transform.localScale = Vector3.one * 0.6f;

                // Balls start in Active partition — they'll fall under gravity
                _world
                    .AddEntity<BallTags.Ball, BallTags.Active>()
                    .Set(new Position(position))
                    .Set(new Velocity(float3.zero))
                    .Set(new RestTimer(0f))
                    .Set(_gameObjectRegistry.Register(go));
            }
        }
    }
}
