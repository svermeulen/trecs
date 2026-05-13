using Unity.Mathematics;

namespace Trecs.Samples.AspectInterfaces
{
    // Aspect interface — a shared component-access contract for "anything
    // that can take a hit." A concrete aspect struct declares IHittable in
    // its base list to inherit IRead<Armor>, IRead<MaxHealth>, IWrite<Health>,
    // and IWrite<HitFlashTime>, and thereby becomes eligible for
    // Combat.TryTakeHit below. The species-specific aspects (EnemyView,
    // BossView) add their own extras on top of this substrate.
    public partial interface IHittable
        : IAspect,
            IRead<Armor>,
            IRead<MaxHealth>,
            IWrite<Health>,
            IWrite<HitFlashTime> { }

    public static class Combat
    {
        // Try to land one hit on anything hittable. Gated by HitFlashTime —
        // no hit lands if the target was hit less than `cooldown` seconds
        // ago. Reads Armor to reduce raw damage (floored at 0), subtracts
        // from Health, stamps HitFlashTime, and removes the entity if its
        // health drops to zero or below. Returns true when the hit
        // actually landed.
        //
        // Three of IHittable's four fields are touched here (Armor, Health,
        // HitFlashTime); MaxHealth rides along in the aspect for callers
        // that need it (EnemyAiSystem's heal clamp, HitFlashPresenter's
        // tint ratio). That's the payoff of the aspect interface — a
        // single `in T` argument instead of shuffling four component refs
        // through every helper and caller's signature.
        public static bool TryTakeHit<T>(
            in T target,
            float rawDamage,
            float cooldown,
            WorldAccessor world
        )
            where T : struct, IHittable
        {
            if (world.ElapsedTime - target.HitFlashTime < cooldown)
            {
                return false;
            }

            float reduced = math.max(0f, rawDamage - target.Armor);
            target.Health -= reduced;
            target.HitFlashTime = world.ElapsedTime;

            if (target.Health <= 0f)
            {
                target.Remove(world);
            }

            return true;
        }
    }
}
