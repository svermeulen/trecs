using Unity.Mathematics;

namespace Trecs.Samples.AspectInterfaces
{
    // Aspect interface — a shared component-access contract for "anything that
    // can take a hit." A concrete aspect struct declares IHittable in its base
    // list to inherit IRead<Armor>, IRead<MaxHealth>, IWrite<Health>, and
    // IWrite<HitFlashTime>, and thereby becomes eligible for every helper
    // below. The species-specific aspects (EnemyView, BossView) add their own
    // extras on top of this substrate.
    public partial interface IHittable
        : IAspect,
            IRead<Armor>,
            IRead<MaxHealth>,
            IWrite<Health>,
            IWrite<HitFlashTime> { }

    // Three small helpers that each read multiple components from the IHittable
    // contract. The point of the aspect interface is carrying this 4-component
    // substrate around as a single `in T` parameter — without it, every helper
    // here would need to take `(in Armor, in MaxHealth, ref Health, ref
    // HitFlashTime, …)`, which gets noisy fast and invites parameter-order
    // bugs when you refactor.
    public static class Combat
    {
        // Subtract the raw incoming damage by the target's armor (floored at 0),
        // apply it to Health, and stamp HitFlashTime so the renderer can flash
        // the GameObject briefly. Uses 3 of 4 components of IHittable.
        public static void TakeHit<T>(in T target, float rawDamage, float now)
            where T : IHittable
        {
            float reduced = math.max(0f, rawDamage - target.Armor);
            target.Health -= reduced;
            target.HitFlashTime = now;
        }

        // Add to Health, clamped at MaxHealth so healers can't over-stack.
        // Uses both Health and MaxHealth from the contract.
        public static void Heal<T>(in T target, float amount)
            where T : IHittable
        {
            target.Health = math.min(target.Health + amount, target.MaxHealth);
        }

        // Normalized health [0..1]. Used by callers that need to make decisions
        // based on fractional HP regardless of the entity's MaxHealth.
        public static float HealthRatio<T>(in T target)
            where T : IHittable => math.clamp(target.Health / target.MaxHealth, 0f, 1f);

        // Sugar for the common "is this thing below half HP?" threshold. Tuned
        // for the sample so the enemy AI pivots from charging to fleeing with
        // a visible window of retreat motion before it exits the zone.
        public static bool IsLowHealth<T>(in T target)
            where T : IHittable => HealthRatio(target) < 0.5f;
    }
}
