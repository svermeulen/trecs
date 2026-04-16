# Sets

Sets let you dynamically mark entities as belonging to a subset, without affecting how they are stored or what components they have. You can think of them like lightweight boolean flags — an entity is either in a set or it isn't — but with efficient iteration over just the members.

## Defining a Set

```csharp
public struct HighlightedParticle : IEntitySet { }
```

Optionally, you can scope a set to entities with specific tags. Adding an entity without the required tags will result in an error:

```csharp
public struct EatingFish : IEntitySet<FrenzyTags.Fish> { }
```

## Registering Sets

Sets must be registered with the world builder:

```csharp
new WorldBuilder()
    .AddSet<HighlightedParticle>()
    .AddSet<SelectedEntities>()
    // ...
```

## Adding and Removing Entities

### Deferred

The standard API queues changes that take effect at the next submission. Safe to call during iteration:

```csharp
World.SetAdd<HighlightedParticle>(particle.EntityIndex);
World.SetRemove<HighlightedParticle>(particle.EntityIndex);
```

### Immediate

`AddImmediate` / `RemoveImmediate` take effect right away. These are thread-safe and can be used from both the main thread and jobs:

```csharp
// Main thread
World.Set<HighlightedParticle>().Write.AddImmediate(entityIndex);
World.Set<HighlightedParticle>().Write.RemoveImmediate(entityIndex);

// In a job (via NativeSetWrite)
highlighted.AddImmediate(entityIndex);
highlighted.RemoveImmediate(entityIndex);
```

## Querying by Set

### With ForEachEntity

```csharp
[ForEachEntity(Set = typeof(HighlightedParticle))]
void Execute(in ParticleView particle)
{
    // Only visits entities in the HighlightedParticle set
}
```

### With Aspect Queries

```csharp
foreach (var particle in ParticleView.Query(World).InSet<HighlightedParticle>())
{
    particle.Color = Color.yellow;
}
```

### Counting

```csharp
int highlighted = World.Query().InSet<HighlightedParticle>().Count();
```

## When to Use Sets vs Tags

| | Tags | Sets |
|---|---|---|
| **Cost of change** | Structural change (deferred, moves data) | Lightweight add/remove from index |
| **Iteration** | All entities with that tag are contiguous in memory | Sparse — only set members are visited |
| **Best for** | Core identity, maximum cache locality | Dynamic membership, temporary flags, filtering |

Both tags (via [partitions](../core/templates.md#partitions)) and sets can represent state, but the trade-offs differ. Tag changes move entity data in memory, giving you dense iteration. Set changes are cheap but iteration is sparse. See [Entity Subset Patterns](../recipes/entity-subset-patterns.md) for a deeper comparison.
