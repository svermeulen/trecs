using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.ReactiveEvents
{
    /// <summary>
    /// Spawns a new Bubble entity every <c>_spawnInterval</c> seconds at a
    /// random position. The accompanying GameObject is created here and
    /// destroyed by the OnRemoved observer — a natural fit for the reactive
    /// pattern, since entity lifetime is the source of truth.
    ///
    /// Mutable per-tick state (the spawn cooldown) lives on a global
    /// component, not a system member variable, so the world's state is
    /// fully captured by its entities and globals. This is a prerequisite
    /// for deterministic replay, serialization, and rollback.
    /// </summary>
    public partial class BubbleSpawnerSystem : ISystem
    {
        readonly float _spawnInterval;
        readonly GameObjectRegistry _registry;

        public BubbleSpawnerSystem(float spawnInterval, GameObjectRegistry registry)
        {
            _spawnInterval = spawnInterval;
            _registry = registry;
        }

        public void Execute()
        {
            ref var state = ref World.GlobalComponent<State>().Write;

            state.Cooldown -= World.DeltaTime;
            while (state.Cooldown <= 0f)
            {
                state.Cooldown += _spawnInterval;
                Spawn();
            }
        }

        void Spawn()
        {
            var position = new float3(
                (World.Rng.Next() - 0.5f) * 8f,
                0f,
                (World.Rng.Next() - 0.5f) * 8f
            );
            var velocity = new float3(
                (World.Rng.Next() - 0.5f) * 2f,
                2f + World.Rng.Next() * 2f,
                (World.Rng.Next() - 0.5f) * 2f
            );
            float lifetime = 1.5f + World.Rng.Next() * 2f;

            var go = SampleUtil.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Bubble";
            go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);

            World
                .AddEntity<SampleTags.Bubble>()
                .Set(new Position(position))
                .Set(new Velocity(velocity))
                .Set(new Lifetime(lifetime))
                .Set(_registry.Register(go));
        }

        public partial struct State : IEntityComponent
        {
            public float Cooldown;
        }
    }
}
