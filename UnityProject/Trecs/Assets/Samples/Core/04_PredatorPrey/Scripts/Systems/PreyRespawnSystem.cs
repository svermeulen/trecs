using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.PredatorPrey
{
    /// <summary>
    /// Maintains a minimum prey count by spawning new prey when needed.
    /// </summary>
    public partial class PreyRespawnSystem : ISystem
    {
        readonly SampleSettings _settings;
        readonly GameObjectRegistry _gameObjectRegistry;

        public PreyRespawnSystem(SampleSettings settings, GameObjectRegistry gameObjectRegistry)
        {
            _settings = settings;
            _gameObjectRegistry = gameObjectRegistry;
        }

        public void Execute()
        {
            int currentCount = World.CountEntitiesWithTags<SampleTags.Prey>();
            int toSpawn = _settings.PreyCount - currentCount;

            for (int i = 0; i < toSpawn; i++)
            {
                float angle = World.Rng.Next() * 2f * math.PI;
                float radius = World.Rng.Next() * _settings.SpawnRadius;
                var position = new float3(math.cos(angle) * radius, 0.5f, math.sin(angle) * radius);

                var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Prey";
                go.transform.position = (Vector3)position;
                go.GetComponent<Renderer>().material.color = Color.cyan;

                float velocityAngle = World.Rng.Next() * 2f * math.PI;

                World
                    .AddEntity<SampleTags.Prey>()
                    .Set(new Position(position))
                    .Set(
                        new MoveDirection(
                            new float3(math.cos(velocityAngle), 0, math.sin(velocityAngle))
                        )
                    )
                    .Set(new Speed(_settings.PreySpeed))
                    .Set(_gameObjectRegistry.Register(go));
            }
        }
    }
}
