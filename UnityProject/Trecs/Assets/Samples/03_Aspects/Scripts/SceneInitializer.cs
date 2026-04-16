using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Aspects
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
            float halfSize = _settings.AreaSize / 2f;

            for (int i = 0; i < _settings.BoidCount; i++)
            {
                float angle = rng.Next() * 2f * math.PI;
                var velocity = new float3(math.cos(angle), 0, math.sin(angle));
                float speed =
                    _settings.MinSpeed + rng.Next() * (_settings.MaxSpeed - _settings.MinSpeed);
                var position = new float3(
                    rng.NextFloat(-halfSize, halfSize),
                    0.25f,
                    rng.NextFloat(-halfSize, halfSize)
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = $"Boid_{i}";
                go.transform.localScale = new Vector3(0.3f, 0.3f, 0.6f);
                go.transform.position = (Vector3)position;

                var renderer = go.GetComponent<Renderer>();
                renderer.material.color = Color.HSVToRGB(rng.Next(), 0.7f, 0.9f);

                _world
                    .AddEntity<SampleTags.Boid>()
                    .Set(new Position(position))
                    .Set(new Velocity(velocity))
                    .Set(new Speed(speed))
                    .Set(_gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
