using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.SpawnAndDestroy
{
    public partial class SpawnSystem : ISystem
    {
        readonly float _spawnInterval;
        readonly float _lifetime;
        readonly float _spawnRadius;
        readonly GameObjectRegistry _gameObjectRegistry;

        float _timer;

        public SpawnSystem(
            float spawnInterval,
            float lifetime,
            float spawnRadius,
            GameObjectRegistry gameObjectRegistry
        )
        {
            _spawnInterval = spawnInterval;
            _lifetime = lifetime;
            _spawnRadius = spawnRadius;
            _gameObjectRegistry = gameObjectRegistry;
        }

        public void Execute()
        {
            _timer += World.DeltaTime;

            while (_timer >= _spawnInterval)
            {
                _timer -= _spawnInterval;
                SpawnSphere();
            }
        }

        void SpawnSphere()
        {
            float angle = World.Rng.Next() * 2f * math.PI;
            float radius = World.Rng.Next() * _spawnRadius;
            var position = new float3(math.cos(angle) * radius, 0.5f, math.sin(angle) * radius);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Sphere";
            go.transform.position = (Vector3)position;

            // Give each sphere a random color
            var renderer = go.GetComponent<Renderer>();
            renderer.material.color = new Color(
                World.Rng.Next(),
                World.Rng.Next(),
                World.Rng.Next()
            );

            World
                .AddEntity<SampleTags.Sphere>()
                .Set(new Position(position))
                .Set(new Lifetime(_lifetime))
                .Set(_gameObjectRegistry.Register(go));
        }
    }
}
