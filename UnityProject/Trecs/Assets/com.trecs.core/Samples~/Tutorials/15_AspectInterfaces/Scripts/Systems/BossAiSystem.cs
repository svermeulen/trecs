using Unity.Mathematics;

namespace Trecs.Samples.AspectInterfaces
{
    public partial class BossAiSystem : ISystem
    {
        readonly float3 _zoneCenter;
        readonly float _zoneRadius;
        readonly float _damagePerHit;
        readonly float _hitInterval;
        readonly float _enragedRegenPerSecond;

        public BossAiSystem(
            float3 zoneCenter,
            float zoneRadius,
            float damagePerHit,
            float hitInterval,
            float enragedRegenPerSecond
        )
        {
            _zoneCenter = zoneCenter;
            _zoneRadius = zoneRadius;
            _damagePerHit = damagePerHit;
            _hitInterval = hitInterval;
            _enragedRegenPerSecond = enragedRegenPerSecond;
        }

        [ForEachEntity(Tag = typeof(SampleTags.Boss))]
        void Execute(in BossView boss)
        {
            float3 toCenter = _zoneCenter - boss.Position;
            bool insideZone = math.lengthsq(toCenter) < _zoneRadius * _zoneRadius;

            // Discrete damage pulses on the same cadence as enemies. Boss's
            // Armor is higher so each pulse's net damage is smaller — combined
            // with the enraged regen, health oscillates near the enrage
            // threshold rather than dying outright.
            if (insideZone && World.ElapsedTime - boss.HitFlashTime >= _hitInterval)
            {
                Combat.TakeHit(boss, _damagePerHit, World.ElapsedTime);
            }

            // Boss-specific: enrage scales 0..1 with damage taken. Uses the
            // shared HealthRatio helper as a subroutine, but stores the result
            // in a boss-only component so other systems (or the renderer) can
            // read it.
            boss.EnrageLevel = 1f - Combat.HealthRatio(boss);

            // Boss-specific: once enraged, self-heal continuously. Regen is
            // per-second (not per-pulse), so it's applied every frame — the
            // tuning makes the combined regen outpace damage slightly when
            // enraged, so the boss heals back across the threshold and
            // disables its own regen, then takes damage back down, etc.
            if (boss.EnrageLevel > 0.5f)
            {
                Combat.Heal(boss, _enragedRegenPerSecond * World.DeltaTime);
            }
        }

        // Concrete aspect. IHittable contributes the shared combat substrate;
        // the boss adds Position (stationary, read-only) and EnrageLevel
        // (boss-only state the renderer can pick up).
        partial struct BossView : IHittable, IRead<Position>, IWrite<EnrageLevel> { }
    }
}
