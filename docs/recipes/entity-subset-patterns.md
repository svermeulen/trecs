# Entity Subset Patterns

There are three main approaches to filtering entities at runtime. Each has different performance characteristics and use cases.

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

**Pros:** Simple, no setup required.
**Cons:** Iterates all entities, including those that don't match.

## Approach B: Template Partitions (Group Swaps)

Use `IHasPartition` to move entities between groups:

```csharp
// Template with partitions
public partial class EnemyEntity : ITemplate,
    IHasTags<GameTags.Enemy>,
    IHasPartition<GameTags.Alive>,
    IHasPartition<GameTags.Dead>
{
    public Health Health;
    public Position Position;
}

// Transition
World.MoveTo<GameTags.Enemy, GameTags.Dead>(entity.EntityIndex);

// Iterate only dead enemies
[ForEachEntity(Tags = new[] { typeof(GameTags.Enemy), typeof(GameTags.Dead) })]
void Execute(in DeadEnemy enemy) { ... }
```

**Pros:** Dense iteration — only matching entities are visited. Cache-friendly.
**Cons:** Moving between groups copies component data. Adding dimensions multiplies the number of groups (2^N for N boolean partitions).

## Approach C: Sets (Indexed Subsets)

Use sets for dynamic, sparse membership:

```csharp
public struct DeadEnemies : IEntitySet<GameTags.Enemy> { }

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
| **Iteration** | All entities | Dense (fast) | Sparse (indexed) |
| **Group count** | No increase | 2^N per dimension | No increase |
| **Best for** | Rare checks, simple conditions | Core lifecycle partitions (1-2 dimensions) | Dynamic membership, many dimensions |

### Rules of Thumb

- **1-2 boolean partition dimensions** → Template partitions. Dense iteration is fast, and the group count stays manageable.
- **3+ dimensions, or many categories** → Sets. Avoids combinatorial explosion of groups.
- **One-off checks** → Component value check. No need for infrastructure if you're just checking occasionally.

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

You can combine approaches. Use template partitions for core lifecycle (1-2 dimensions) and sets for dynamic categorization:

```csharp
// Template partitions for core lifecycle
public partial class Enemy : ITemplate,
    IHasTags<GameTags.Enemy>,
    IHasPartition<GameTags.Alive>,
    IHasPartition<GameTags.Dead>
{ ... }

// Sets for dynamic filters
public struct PoisonedEnemies : IEntitySet<GameTags.Enemy> { }
public struct VisibleEnemies : IEntitySet<GameTags.Enemy> { }
public struct TargetedEnemies : IEntitySet<GameTags.Enemy> { }
```
