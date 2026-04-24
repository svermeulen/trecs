using Unity.Mathematics;

namespace Trecs.Samples.AspectInterfaces
{
    public partial class BossAiSystem : ISystem
    {
        readonly SampleSettings _settings;

        public BossAiSystem(SampleSettings settings)
        {
            _settings = settings;
        }

        // [ForEachEntity] (not [SingleEntity]) so the system is a no-op
        // after the boss dies — [SingleEntity] would assert on zero matches.
        [ForEachEntity(Tag = typeof(SampleTags.Boss))]
        void Execute(in BossView boss)
        {
            float collisionSqr = _settings.CollisionDistance * _settings.CollisionDistance;
            float3 chaseTarget = default;
            float? lowestHealth = null;

            // Single pass over enemies: track the weakest (so the boss can
            // chase it) and apply damage to the boss for each enemy in
            // range. The enemy's own system handles the mirrored hit on
            // its side — this loop doesn't touch enemy state at all.
            foreach (var enemy in EnemyTargetView.Query(World).WithTags<SampleTags.Enemy>())
            {
                if (lowestHealth == null || enemy.Health < lowestHealth)
                {
                    lowestHealth = enemy.Health;
                    chaseTarget = enemy.Position;
                }

                if (math.distancesq(boss.Position, enemy.Position) <= collisionSqr)
                {
                    if (
                        Combat.TryTakeHit(
                            boss,
                            _settings.DamagePerHit,
                            _settings.BossHitCooldown,
                            World
                        )
                    )
                    {
                        break;
                    }
                }
            }

            // Advance toward the weakest enemy, but stop just outside the
            // collision radius — otherwise the boss can step onto the
            // target exactly, after which normalizesafe(zero) pins both
            // sides at the coincident position.
            if (lowestHealth != null)
            {
                // XZ-only direction — otherwise the Y delta between boss
                // and enemies bakes into the step and the boss slowly
                // drifts vertically.
                float3 toTarget = chaseTarget - boss.Position;
                toTarget.y = 0f;
                float distSqr = math.lengthsq(toTarget);
                if (distSqr > collisionSqr)
                {
                    float3 dir = toTarget * math.rsqrt(distSqr);
                    boss.Position += dir * _settings.BossChaseSpeed * World.DeltaTime;
                }
            }
        }

        // Boss's own aspect. IHittable contributes the combat substrate;
        // Position is written as the boss chases the weakest enemy.
        partial struct BossView : IHittable, IWrite<Position> { }

        // Read-only view of enemies — used to pick the weakest and to
        // check collision distance. Damage application happens in the
        // enemy's own system, so no IHittable here.
        partial struct EnemyTargetView : IAspect, IRead<Position, Health> { }
    }
}
