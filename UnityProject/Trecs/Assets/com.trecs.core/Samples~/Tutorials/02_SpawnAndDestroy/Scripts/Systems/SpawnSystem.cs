using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.SpawnAndDestroy
{
    public partial class SpawnSystem : ISystem
    {
        readonly float _spawnInterval;
        readonly float _lifetime;
        readonly float _spawnRadius;

        public SpawnSystem(float spawnInterval, float lifetime, float spawnRadius)
        {
            _spawnInterval = spawnInterval;
            _lifetime = lifetime;
            _spawnRadius = spawnRadius;
        }

        void Execute([SingleEntity(typeof(TrecsTags.Globals))] ref State state)
        {
            state.Timer += World.DeltaTime;

            while (state.Timer >= _spawnInterval)
            {
                state.Timer -= _spawnInterval;
                SpawnSphere();
            }
        }

        void SpawnSphere()
        {
            float angle = World.Rng.Next() * 2f * math.PI;
            float radius = World.Rng.Next() * _spawnRadius;
            var position = new float3(math.cos(angle) * radius, 0.5f, math.sin(angle) * radius);

            // World.Rng provides deterministic randomness that works with recording/playback
            var color = new Color(World.Rng.Next(), World.Rng.Next(), World.Rng.Next());

            World
                .AddEntity<SampleTags.Sphere>()
                .Set(new Position(position))
                .Set(new Lifetime(_lifetime))
                .Set(new ColorComponent(color));
        }

        public partial struct State : IEntityComponent
        {
            public float Timer;
        }
    }
}
