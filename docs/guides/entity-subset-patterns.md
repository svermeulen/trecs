# Entity Subset Patterns

There are three main approaches to filtering entities at runtime. Each has different performance characteristics and use cases.

## Approach A: check component values

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

**Pros:** Simple, no setup required.

**Cons:** Visits every entity the query matches (every group with the requested components when using `MatchByComponents`, or every entity in the tag-scoped groups otherwise), including those that fail the branch. Only a real problem when the population is large and the check sits in a hot loop.

## Approach B: [sets](../entity-management/sets.md) (indexed subsets)

Use sets for dynamic, sparse membership. Sets must be registered with the world builder via `AddSet<T>()`:

```csharp
public struct DeadEnemies : IEntitySet { }

// Add to set
World.Set<DeadEnemies>().Defer.Add(entity.Handle);

// Iterate set members
[ForEachEntity(Set = typeof(DeadEnemies))]
void Execute(in DeadEnemy enemy) { ... }
```

**Pros:** No data movement. Unlimited dimensions without group explosion. Fast add/remove.
**Cons:** Sparse iteration (indexed access, less cache-friendly than dense).

## Approach C: template [partitions](../core/templates.md#partitions)

Use `IPartitionedBy<...>` to define mutually exclusive states that entities can transition between. Arity 1 = presence/absence; arity ≥ 2 = explicit variants.

```csharp
// Presence/absence: one tag, two partitions (alive vs. not)
public partial class EnemyEntity : ITemplate,
    ITagged<GameTags.Enemy>,
    IPartitionedBy<GameTags.Alive>
{
    Health Health;
    Position Position;
}

// Transition (from inside an Execute)
entity.UnsetTag<GameTags.Alive>(World);   // → dead

// Iterate only living enemies
[ForEachEntity(typeof(GameTags.Enemy), typeof(GameTags.Alive))]
void Execute(in LivingEnemy enemy) { ... }

// Iterate only dead enemies
[ForEachEntity(typeof(GameTags.Enemy), Without = typeof(GameTags.Alive))]
void Execute(in DeadEnemy enemy) { ... }
```

**Pros:** Dense iteration — only matching entities are visited. Cache-friendly.

**Cons:** Moving between groups copies component data. Adding dimensions multiplies the number of groups.

## Decision guide

!!! tip "Start simple — partitions are an optimization"
    Branching on a component value (Approach A) is fast enough for most gameplay code and a fine default. Reach for **sets** whenever the subset is itself a concept you want to name, query, or iterate. **Partitions** are a cache-locality optimization for very large populations — pick them when the profiler or population size calls for it, not as a general way to model state.

| Factor | Component Check | Sets | Template Partitions |
|--------|----------------|------|---------------------|
| **Setup** | None | Set struct + registration | Template + tags |
| **Change cost** | None (just data) | Index add/remove | Component copy |
| **Iteration** | Visits every entity the query matches (then branches) | Sparse (indexed lookup per member) | Dense (only matching entities visited) |
| **Group count** | No increase | No increase | 2^N per dimension |
| **Best for** | The default — gameplay code where the condition is local to the component data | Dynamic membership, many dimensions, or any subset that's itself a first-class concept | Hot iteration over very large populations where cache locality dominates the cost |

## Combinatorial explosion

With template partitions, each dimension doubles the number of groups:

| Dimensions | Groups |
|-----------|--------|
| 1 (Alive/Dead) | 2 |
| 2 (Alive/Dead × Visible/Hidden) | 4 |
| 3 (+ Poisoned/Healthy) | 8 |
| 4 | 16 |

At 3+ dimensions, prefer sets — they don't create additional groups.

## Mixing approaches

Nothing forces you to pick one approach per template. A typical mix is to use **partitions** when a lifecycle split (e.g. `Alive` / dead) is hot enough that splitting the storage is worth it, and **sets** for everything else — dynamic categorizations, designer-meaningful subsets, multi-dimensional filters. Component-value branching covers the rest.

```csharp
// Partitions: only because the alive/dead split is a measured hot path.
public partial class Enemy : ITemplate,
    ITagged<GameTags.Enemy>,
    IPartitionedBy<GameTags.Alive>
{ ... }

// Sets: design-level subsets that systems want to query directly.
public struct PoisonedEnemies : IEntitySet<GameTags.Enemy> { }
public struct VisibleEnemies : IEntitySet<GameTags.Enemy> { }
public struct TargetedEnemies : IEntitySet<GameTags.Enemy> { }
```
