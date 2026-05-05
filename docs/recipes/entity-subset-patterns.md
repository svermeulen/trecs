# Entity Subset Patterns

There are three main approaches to filtering entities at runtime. Each has different performance characteristics and use cases.

!!! tip "Start simple — partitions in particular are an optimization"
    Branching on a component value inside a normal iteration (Approach A) is the simplest option and is fast enough for most gameplay code, so it's a fine default. **Sets** are also a legitimate design tool whenever the subset is itself something you want to name, query, count, or iterate as a first-class concept — don't hesitate to reach for them when they fit the shape of the problem. **Partitions**, on the other hand, are primarily a performance optimization for cache-friendly dense iteration over very large populations; pick them when the profiler (or population size) calls for it, not as a general way to model state.

## Approach A: Check Component Values

Iterate all entities and check a condition:

```csharp
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Health health)
{
    if (health.Current <= 0)
    {
        // Handle dead entity
    }
}
```

**Pros:** Simple, no setup required. State stays colocated with the data it describes, so there's nothing to keep in sync.
**Cons:** Visits every entity the query matches (every group with the requested components when using `MatchByComponents`, or every entity in the tag-scoped groups otherwise), including those that fail the branch. Only a real problem when the population is large *and* the check sits in a hot loop.

## Approach B: Template [Partitions](../core/templates.md#partitions)

Use `IHasPartition` to define mutually exclusive states that entities can transition between:

```csharp
// Template with partitions
public partial class EnemyEntity : ITemplate,
    IHasTags<GameTags.Enemy>,
    IHasPartition<GameTags.Alive>,
    IHasPartition<GameTags.Dead>
{
    Health Health;
    Position Position;
}

// Transition
World.MoveTo<GameTags.Enemy, GameTags.Dead>(entity.EntityIndex);

// Iterate only dead enemies
[ForEachEntity(Tags = new[] { typeof(GameTags.Enemy), typeof(GameTags.Dead) })]
void Execute(in DeadEnemy enemy) { ... }
```

**Pros:** Dense iteration — only matching entities are visited. Cache-friendly.
**Cons:** Moving between groups copies component data. Adding dimensions multiplies the number of groups (2^N for N boolean partitions).

## Approach C: [Sets](../entity-management/sets.md) (Indexed Subsets)

Use sets for dynamic, sparse membership. Sets must be registered with the world builder via `AddSet<T>()`:

```csharp
public struct DeadEnemies : IEntitySet { }

// Add to set
World.SetAdd<DeadEnemies>(entity.EntityIndex);

// Iterate set members
[ForEachEntity(Set = typeof(DeadEnemies))]
void Execute(in DeadEnemy enemy) { ... }
```

**Pros:** No data movement. Unlimited dimensions without group explosion. Fast add/remove.
**Cons:** Sparse iteration (indexed access, less cache-friendly than dense).

## Decision Guide

| Factor | Component Check | Template Partitions | Sets |
|--------|----------------|---------------------|------|
| **Setup** | None | Template + tags | Set struct + registration |
| **Change cost** | None (just data) | Component copy | Index add/remove |
| **Iteration** | Visits every entity the query matches (then branches) | Dense (only matching entities visited) | Sparse (indexed lookup per member) |
| **Group count** | No increase | 2^N per dimension | No increase |
| **Best for** | The default — gameplay code where the condition is local to the component data | Hot iteration over very large populations where cache locality dominates the cost | Dynamic membership, many dimensions, or any subset that's itself a first-class concept |

### Rules of Thumb

Pick the approach that matches the shape of the problem. Component checks are the simplest default, sets are a reasonable choice whenever a subset is itself a meaningful concept, and partitions are reserved for when iteration cost actually matters.

- **Simple per-iteration filtering, membership not needed elsewhere** → Component value check. No registration, no transitions, no extra memory. Just `if` inside the loop.
- **The subset is a named concept systems want to query, iterate, or count directly** → Sets. Use them freely when they model your design well — "visible enemies", "units under player control", "entities currently selected" are all natural fits.
- **One system needs to flag a subset for several downstream systems in the same frame** → Sets, used as per-frame scratch storage. Clear the set at the top of the producer, fill it with `AddImmediate`, and have consumers iterate the set instead of re-running the producer's filter. See [Per-Frame Staging](../entity-management/sets.md#per-frame-staging) for the full pattern (and why the immediate APIs are required here).
- **3+ dimensions, or many categories** → Sets. Avoids combinatorial explosion of groups.
- **Very large entity populations (thousands+) where a hot iteration shows up in the profiler** → Template partitions. The dense-array layout gives you cache-friendly iteration, but at the cost of data movement on transitions and extra groups per dimension. Treat this as a performance optimization, not a design primitive — the partition boundary should reflect a real hot path, not just a conceptual state split.

## Combinatorial Explosion

With template partitions, each dimension doubles the number of groups:

| Dimensions | Groups |
|-----------|--------|
| 1 (Alive/Dead) | 2 |
| 2 (Alive/Dead × Visible/Hidden) | 4 |
| 3 (+ Poisoned/Healthy) | 8 |
| 4 | 16 |

At 3+ dimensions, prefer sets — they don't create additional groups.

## Mixing Approaches

Nothing forces you to pick one approach per template. A typical mix is to use **partitions** when a lifecycle split (e.g. `Alive` / `Dead`) is hot enough that splitting the storage is worth it, and **sets** for everything else — dynamic categorizations, designer-meaningful subsets, multi-dimensional filters. Component-value branching covers the rest.

```csharp
// Partitions: only because Alive/Dead iteration is a measured hot path.
public partial class Enemy : ITemplate,
    IHasTags<GameTags.Enemy>,
    IHasPartition<GameTags.Alive>,
    IHasPartition<GameTags.Dead>
{ ... }

// Sets: design-level subsets that systems want to query directly.
public struct PoisonedEnemies : IEntitySet<GameTags.Enemy> { }
public struct VisibleEnemies : IEntitySet<GameTags.Enemy> { }
public struct TargetedEnemies : IEntitySet<GameTags.Enemy> { }
```
