# 15 — Aspect Interfaces

Composable, reusable aspect contracts so different templates can share the same helper logic without duplication.

**Source:** `Samples/15_AspectInterfaces/`

## What it does

An arena has one **boss** and a flock of **enemies** that charge and flee. They share nothing structurally — the boss has no `Mood`, `ChaseSpeed`, or `FleeEndTime`; enemies have no boss-only fields — but both can take a hit. An **aspect interface** (`IHittable`) expresses "anything that can take a hit" once, then a single `Combat.TryTakeHit<T>` helper serves both species.

## The idea

A regular aspect is a concrete `partial struct` declaring `IRead<>` / `IWrite<>` constraints. An **aspect interface** is a `partial interface` declaring the same constraints, inheritable by concrete aspects that compose the shared contract with species-specific extras:

```csharp
public partial interface IHittable
    : IAspect,
        IRead<Armor>,
        IRead<MaxHealth>,
        IWrite<Health>,
        IWrite<HitFlashTime> { }
```

Any concrete aspect that lists `IHittable` in its base list inherits the four component accesses and becomes usable wherever an `IHittable` is expected:

```csharp
// Concrete enemy aspect — inherits IHittable + adds enemy-only extras
partial struct EnemyView
    : IHittable,
        IWrite<Position>,
        IRead<ChaseSpeed>,
        IWrite<Mood>,
        IWrite<FleeEndTime> { }

// Concrete boss aspect — inherits IHittable + adds boss-only Position write
partial struct BossView : IHittable, IWrite<Position> { }
```

## The payoff — generic helpers

Because both aspects satisfy `IHittable`, `Combat.TryTakeHit` is written **once** with a generic constraint and called from both the enemy AI and the boss path:

```csharp
public static bool TryTakeHit<T>(
    in T target,
    float rawDamage,
    float cooldown,
    WorldAccessor world
)
    where T : struct, IHittable
{
    if (world.ElapsedTime - target.HitFlashTime < cooldown)
        return false;

    float reduced = math.max(0f, rawDamage - target.Armor);
    target.Health -= reduced;
    target.HitFlashTime = world.ElapsedTime;

    if (target.Health <= 0f)
        target.Remove(world);

    return true;
}
```

Call sites:

```csharp
// In EnemyAiSystem:
Combat.TryTakeHit(enemy, damage, cooldown, World);

// In BossAiSystem:
Combat.TryTakeHit(boss, damage, cooldown, World);
```

Without the aspect interface, this helper would either take four individual component refs (`ref Health`, `in Armor`, …) on every call, or be written twice — once per species. The aspect interface collapses both into a single `in T` argument.

## Cross-species rendering

The other half of the pattern: a `HitFlashRenderer` that iterates **by components, not tags**, rendering any entity with the required shape regardless of species:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class HitFlashRenderer : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(
        in GameObjectId id,
        in Position position,
        in Health health,
        in MaxHealth maxHealth,
        in HitFlashTime hitFlashTime,
        in ColorComponent baseColor
    )
    {
        // Tint white during the flash window, otherwise base colour × HP ratio
    }
}
```

Enemies and the boss share this component set, so one renderer drives both. Aspect interfaces and `MatchByComponents` are two halves of the same idea — behaviour hangs off *capabilities*, not *identities*.

## When to reach for this

- Multiple templates participate in the same mechanic (damage, pickups, AI targeting, selection, saving) but differ in their extras.
- You want one helper method or system to handle all of them without copy-paste or tag-dispatch branches.
- The shared component set is large enough that passing individual refs would bloat every signature.

If only one concrete aspect needs the component set, skip the interface and use a regular aspect.

## Concepts introduced

- **Aspect interfaces** — `partial interface` declarations inheriting `IAspect` + `IRead<>` / `IWrite<>` to express a shared capability contract
- **Generic aspect constraints** — `where T : struct, IHittable` on helper methods, so one implementation serves every aspect satisfying the interface
- **Component-shaped iteration** — `MatchByComponents = true` iterates every entity with the required components, independent of tag
