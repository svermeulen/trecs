using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    /// <summary>
    /// Maintains a minimum prey count by spawning new prey when needed.
    /// </summary>
    [ExecutesAfter(typeof(CatchSystem))]
    public partial class PreyRespawnSystem : ISystem
    {
        readonly int _targetPreyCount;
        readonly float _spawnRadius;
        readonly GameObjectRegistry _gameObjectRegistry;

        public PreyRespawnSystem(
            int targetPreyCount,
            float spawnRadius,
            GameObjectRegistry gameObjectRegistry
        )
        {
            _targetPreyCount = targetPreyCount;
            _spawnRadius = spawnRadius;
            _gameObjectRegistry = gameObjectRegistry;
        }

        public void Execute()
        {
            int currentCount = World.CountEntitiesWithTags<SampleTags.Prey>();
            int toSpawn = _targetPreyCount - currentCount;

            for (int i = 0; i < toSpawn; i++)
            {
                float angle = World.Rng.Next() * 2f * math.PI;
                float radius = World.Rng.Next() * _spawnRadius;
                var position = new float3(math.cos(angle) * radius, 0.5f, math.sin(angle) * radius);

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Prey";
                go.transform.position = (Vector3)position;
                go.GetComponent<Renderer>().material.color = Color.cyan;

                World
                    .AddEntity<SampleTags.Prey>()
                    .Set(new Position(position))
                    .Set(_gameObjectRegistry.Register(go))
                    .AssertComplete();
            }
        }
    }
}
