using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Aspects
{
    public class SceneInitializer
    {
        readonly WorldAccessor _world;
        readonly SampleSettings _settings;
        readonly RenderableGameObjectManager _goManager;

        public SceneInitializer(
            World world,
            SampleSettings settings,
            RenderableGameObjectManager goManager
        )
        {
            _world = world.CreateAccessor(AccessorRole.Fixed);
            _settings = settings;
            _goManager = goManager;
        }

        public void Initialize()
        {
            _goManager.RegisterFactory(AspectsPrefabs.Boid, CreateBoidGameObject);

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

                var color = Color.HSVToRGB(rng.Next(), 0.7f, 0.9f);

                _world
                    .AddEntity<SampleTags.Boid>()
                    .Set(new Position(position))
                    .Set(new Velocity(velocity))
                    .Set(new Speed(speed))
                    .Set(new ColorComponent(color));
            }
        }

        static GameObject CreateBoidGameObject()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Capsule);
            go.transform.localScale = new Vector3(0.3f, 0.3f, 0.6f);
            return go;
        }
    }
}
