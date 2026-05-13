using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
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
            _goManager.RegisterFactory(PredatorPreyPrefabs.Predator, CreatePredator);
            _goManager.RegisterFactory(PredatorPreyPrefabs.Prey, CreatePrey);

            var rng = _world.FixedRng;

            for (int i = 0; i < _settings.PredatorCount; i++)
            {
                var position = new float3(
                    rng.NextFloat(-_settings.SpawnRadius, _settings.SpawnRadius),
                    0.5f,
                    rng.NextFloat(-_settings.SpawnRadius, _settings.SpawnRadius)
                );

                _world
                    .AddEntity<SampleTags.Predator>()
                    .Set(new Position(position))
                    .Set(new Speed(_settings.PredatorSpeed));
            }
        }

        static GameObject CreatePredator()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Capsule);
            go.transform.localScale = new Vector3(0.6f, 0.6f, 1.2f);
            go.GetComponent<Renderer>().material.color = Color.red;
            return go;
        }

        static GameObject CreatePrey()
        {
            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.GetComponent<Renderer>().material.color = Color.cyan;
            return go;
        }
    }
}
