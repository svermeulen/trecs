using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    public partial class SpawnSystem : ISystem
    {
        readonly float _spawnInterval;
        readonly float _lifetime;
        readonly float _spawnRadius;
        readonly Vector3 _origin;
        readonly Color _color;
        readonly PrimitiveType _primitive;
        readonly GameObjectRegistry _gameObjectRegistry;

        float _timer;

        public SpawnSystem(
            float spawnInterval,
            float lifetime,
            float spawnRadius,
            Vector3 origin,
            Color color,
            PrimitiveType primitive,
            GameObjectRegistry gameObjectRegistry
        )
        {
            _spawnInterval = spawnInterval;
            _lifetime = lifetime;
            _spawnRadius = spawnRadius;
            _origin = origin;
            _color = color;
            _primitive = primitive;
            _gameObjectRegistry = gameObjectRegistry;
        }

        public void Execute()
        {
            _timer += World.DeltaTime;

            while (_timer >= _spawnInterval)
            {
                _timer -= _spawnInterval;
                Spawn();
            }
        }

        void Spawn()
        {
            float angle = World.Rng.Next() * 2f * math.PI;
            float radius = World.Rng.Next() * _spawnRadius;
            var position =
                (float3)_origin
                + new float3(math.cos(angle) * radius, 0.5f, math.sin(angle) * radius);

            var go = SampleUtil.CreatePrimitive(_primitive);
            go.name = _primitive.ToString();
            go.transform.position = (Vector3)position;
            go.GetComponent<Renderer>().material.color = _color;

            World
                .AddEntity<SampleTags.Critter>()
                .Set(new Position(position))
                .Set(new Lifetime(_lifetime))
                .Set(_gameObjectRegistry.Register(go));
        }
    }
}
