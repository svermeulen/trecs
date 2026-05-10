using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Trecs.Samples.AspectInterfaces
{
    // Runs after the boss so each enemy sees the boss's post-move position
    // for this frame's collision check.
    [ExecuteAfter(typeof(BossAiSystem))]
    public partial class EnemyAiSystem : ISystem
    {
        readonly SampleSettings _settings;

        public EnemyAiSystem(SampleSettings settings)
        {
            _settings = settings;
        }

        public void Execute()
        {
            // Query the boss once per frame, then forward it to the
            // per-entity method as a pass-through argument — each enemy
            // iteration reads the same boss view without re-querying.
            // When the boss is dead the enemies break into a victory
            // dance instead of freezing in place.
            if (!BossView.Query(World).WithTags<SampleTags.Boss>().TrySingle(out var boss))
            {
                DanceImpl();
                return;
            }
            ExecuteImpl(in boss);
        }

        [ForEachEntity(typeof(SampleTags.Enemy))]
        void DanceImpl(in EnemyView enemy, EntityHandle handle)
        {
            // Per-entity phase from EntityHandle so each enemy bobs out
            // of sync — half the flock on the way up while the other
            // half is on the way down.
            float phase = handle.Id * 1.7f;
            float t = World.ElapsedTime * _settings.DanceFrequency + phase;
            // abs(sin) so the bob bottoms out at the spawn height
            // instead of dipping below the ground plane.
            float3 pos = enemy.Position;
            pos.y = _settings.EnemySpawnY + math.abs(math.sin(t)) * _settings.DanceAmplitude;
            enemy.Position = pos;
        }

        [ForEachEntity(typeof(SampleTags.Enemy))]
        void ExecuteImpl(
            in EnemyView enemy,
            EntityHandle handle,
            [PassThroughArgument] in BossView boss
        )
        {
            // Passive heal. Tuned so an enemy that just took a hit needs
            // a few seconds of fleeing before it's topped up and ready
            // to charge back in.
            enemy.Health = math.min(
                enemy.Health + _settings.EnemyRegenPerSecond * World.DeltaTime,
                enemy.MaxHealth
            );

            float3 toBoss = boss.Position - enemy.Position;
            float collisionSqr = _settings.CollisionDistance * _settings.CollisionDistance;
            bool inRange = math.lengthsq(toBoss) <= collisionSqr;

            // Detect collision with the boss and handle our own side of
            // the exchange — take damage, flip to Fleeing, and pick a
            // fresh random flee duration so every retreat lasts a
            // slightly different amount of time.
            if (
                inRange
                && Combat.TryTakeHit(
                    enemy,
                    _settings.DamagePerHit,
                    _settings.EnemyHitCooldown,
                    World
                )
            )
            {
                enemy.Mood = EnemyMood.Fleeing;

                // Seed from frame + entity handle so each hit gets a
                // distinct random stream without any persistent RNG
                // state on the system (Trecs systems avoid mutable
                // member state).
                uint seed =
                    1u
                    + (uint)World.Frame * 0x9E3779B9u
                    + (uint)handle.Id * 0x85EBCA6Bu;
                var rng = new Random(seed);
                float duration = rng.NextFloat(
                    _settings.MinFleeDuration,
                    _settings.MaxFleeDuration
                );
                enemy.FleeEndTime = World.ElapsedTime + duration;
            }

            // Time-based flee: flip back to charging once the stamped
            // end-time has passed. Repeated hits re-stamp FleeEndTime,
            // so successive hits naturally extend the retreat.
            if (enemy.Mood == EnemyMood.Fleeing && World.ElapsedTime >= enemy.FleeEndTime)
            {
                enemy.Mood = EnemyMood.Charging;
            }

            // Charging enemies stop at the edge of the collision radius —
            // otherwise, during the HitCooldown window after a hit, they'd
            // keep walking forward into the boss's exact position, where
            // normalizesafe goes to zero and everyone locks up. Fleeing
            // enemies always move away.
            if (enemy.Mood == EnemyMood.Charging && inRange)
            {
                return;
            }

            // Keep movement in the XZ plane — otherwise the Y delta
            // between boss and enemy (they spawn at different heights)
            // bakes into the direction vector and enemies slowly drift
            // vertically with every charge/flee step.
            float3 toBossFlat = toBoss;
            toBossFlat.y = 0f;
            float3 unitToBoss = math.normalizesafe(toBossFlat);
            float3 dir = enemy.Mood == EnemyMood.Fleeing ? -unitToBoss : unitToBoss;
            enemy.Position += dir * enemy.ChaseSpeed * World.DeltaTime;
        }

        // Read-only view of the boss, used for both the collision check
        // and for picking a flee/chase direction.
        partial struct BossView : IAspect, IRead<Position> { }

        // Concrete enemy aspect. IHittable contributes the combat substrate
        // that Combat.TryTakeHit needs, Position is written as the enemy
        // moves, ChaseSpeed is the per-entity speed, and Mood gates
        // charge-vs-flee.
        partial struct EnemyView
            : IHittable,
                IWrite<Position>,
                IRead<ChaseSpeed>,
                IWrite<Mood>,
                IWrite<FleeEndTime> { }
    }
}
