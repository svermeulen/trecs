using Unity.Mathematics;

namespace Trecs.Samples.AspectInterfaces
{
    public partial class EnemyAiSystem : ISystem
    {
        readonly float3 _zoneCenter;
        readonly float _zoneRadius;
        readonly float _damagePerHit;
        readonly float _hitInterval;
        readonly float _outOfZoneRegenPerSecond;

        public EnemyAiSystem(
            float3 zoneCenter,
            float zoneRadius,
            float damagePerHit,
            float hitInterval,
            float outOfZoneRegenPerSecond
        )
        {
            _zoneCenter = zoneCenter;
            _zoneRadius = zoneRadius;
            _damagePerHit = damagePerHit;
            _hitInterval = hitInterval;
            _outOfZoneRegenPerSecond = outOfZoneRegenPerSecond;
        }

        [ForEachEntity(Tag = typeof(SampleTags.Enemy))]
        void Execute(in EnemyView enemy)
        {
            // Enemy-specific: is this enemy currently inside the damage zone?
            float3 toCenter = _zoneCenter - enemy.Position;
            bool insideZone = math.lengthsq(toCenter) < _zoneRadius * _zoneRadius;

            // Discrete damage pulses: inside the zone, the enemy takes one hit
            // every _hitInterval seconds, not one per frame. We gate on
            // HitFlashTime (which Combat.TakeHit stamps) so each entity
            // cooldown is self-contained — no scheduler state needed.
            if (insideZone && World.ElapsedTime - enemy.HitFlashTime >= _hitInterval)
            {
                // Shared subroutine — armor-reduce, health-subtract, stamp flash time.
                Combat.TakeHit(enemy, _damagePerHit, World.ElapsedTime);
            }
            else if (!insideZone)
            {
                // Shared subroutine — same helper the boss uses when enraged.
                // Passive recovery out of the zone gives fled enemies a way to
                // come back, keeping the scene cyclic instead of bleeding them
                // off into the distance forever.
                Combat.Heal(enemy, _outOfZoneRegenPerSecond * World.DeltaTime);
            }

            // Enemy-specific: pick behavior. The threshold check uses the
            // shared IsLowHealth helper, but the *response* (which direction
            // to move, what mood to store) is enemy-specific.
            if (Combat.IsLowHealth(enemy))
            {
                enemy.Mood = EnemyMood.Fleeing;
                // Enemy-specific: run away from the zone center.
                float3 awayDir = math.normalizesafe(-toCenter);
                enemy.Position += awayDir * enemy.ChaseSpeed * World.DeltaTime;
            }
            else if (insideZone)
            {
                enemy.Mood = EnemyMood.Angry;
                // Enemy-specific: charge inward at half speed while tanking damage.
                float3 inwardDir = math.normalizesafe(toCenter);
                enemy.Position += inwardDir * enemy.ChaseSpeed * 0.5f * World.DeltaTime;
            }
            else
            {
                enemy.Mood = EnemyMood.Calm;
                // Enemy-specific: creep slowly toward the zone to pick a fight.
                float3 inwardDir = math.normalizesafe(toCenter);
                enemy.Position += inwardDir * enemy.ChaseSpeed * 0.2f * World.DeltaTime;
            }
        }

        // Concrete aspect. IHittable contributes Armor/MaxHealth/Health/HitFlashTime;
        // the enemy adds Position (movement), ChaseSpeed (enemy-only), Mood (enemy-only).
        partial struct EnemyView : IHittable, IWrite<Position>, IRead<ChaseSpeed>, IWrite<Mood> { }
    }
}
