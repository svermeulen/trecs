using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.MultipleWorlds
{
    public partial class SpawnSystem : ISystem
    {
        public partial struct State : IEntityComponent
        {
            public float Timer;
        }

        readonly float _spawnInterval;
        readonly float _lifetime;
        readonly float _spawnRadius;
        readonly Vector3 _origin;

        public SpawnSystem(float spawnInterval, float lifetime, float spawnRadius, Vector3 origin)
        {
            _spawnInterval = spawnInterval;
            _lifetime = lifetime;
            _spawnRadius = spawnRadius;
            _origin = origin;
        }

        void Execute([SingleEntity(typeof(TrecsTags.Globals))] ref State state)
        {
            state.Timer += World.DeltaTime;

            while (state.Timer >= _spawnInterval)
            {
                state.Timer -= _spawnInterval;
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

            World
                .AddEntity<SampleTags.Critter>()
                .Set(new Position(position))
                .Set(new Lifetime(_lifetime));
        }
    }
}
