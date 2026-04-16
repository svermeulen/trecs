# Sets

Sets provide efficient sparse tracking of entity subsets within groups. Unlike tags (which define groups), sets let you dynamically mark entities without changing their group membership.

## Defining a Set

```csharp
public struct HighlightedParticle : IEntitySet<SampleTags.Particle> { }
```

The generic parameter scopes the set to entities with the `Particle` tag. Global sets (no tag scope) implement `IEntitySet` directly:

```csharp
public struct SelectedEntities : IEntitySet { }
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

Use the deferred API during system execution:

```csharp
// Add entity to set (deferred)
World.SetAdd<HighlightedParticle>(particle.EntityIndex);

// Remove entity from set (deferred)
World.SetRemove<HighlightedParticle>(particle.EntityIndex);
```

These changes are safe to call during iteration — they take effect at the next submission.

## Querying by Set

### With ForEachEntity

```csharp
[ForEachEntity(Set = typeof(HighlightedParticle))]
void Execute(in ParticleView particle)
{
    // Only visits entities in the HighlightedParticle set
}
```

### With Manual Queries

```csharp
// Sparse iteration — only visits set members
foreach (var slice in World.Query().InSet<HighlightedParticle>().GroupSlices())
{
    var colors = World.ComponentBuffer<ColorComponent>(slice.Group);
    foreach (int idx in slice.Indices)
    {
        colors[idx].Value = Color.yellow;
    }
}
```

### Counting

```csharp
int highlighted = World.Query().InSet<HighlightedParticle>().Count();
```

## Example: Wave Highlight

From the Sets sample — dynamically highlight particles near a moving wave:

```csharp
public partial class HighlightSystem : ISystem
{
    public void Execute()
    {
        float waveCenter = math.sin(World.ElapsedTime * 2f) * _gridSize * 0.6f;

        foreach (var particle in ParticleView.Query(World).WithTags<SampleTags.Particle>())
        {
            float dist = math.abs(particle.Position.x - waveCenter);

            if (dist < waveBandWidth)
                World.SetAdd<SampleSets.HighlightedParticle>(particle.EntityIndex);
            else
                World.SetRemove<SampleSets.HighlightedParticle>(particle.EntityIndex);
        }
    }

    partial struct ParticleView : IAspect, IRead<Position>, IWrite<Lifetime> { }
}
```

## When to Use Sets vs Tags

| | Tags | Sets |
|---|---|---|
| **Changes group?** | Yes — entity moves between groups | No — entity stays in same group |
| **Storage** | Dense (all entities in group) | Sparse (tracked subset) |
| **Cost of change** | Component data copied to new group | Add/remove from index |
| **Best for** | Core identity, state transitions | Dynamic membership, filtering |

See [Entity Subset Patterns](../recipes/entity-subset-patterns.md) for a deeper comparison.
