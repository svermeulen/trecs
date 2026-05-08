# Sets

A set is a lightweight membership flag for entities — an entity is either in the set or it isn't. Sets are independent of an entity's components and tags, and iteration visits only the members.

## Defining a Set

```csharp
public struct HighlightedParticle : IEntitySet { }
```

To restrict membership to entities carrying specific tags, use the generic form. Adding an entity without those tags asserts:

```csharp
public struct EatingFish : IEntitySet<FrenzyTags.Fish> { }
```

## Registering Sets

Register each set on the `WorldBuilder`:

```csharp
new WorldBuilder()
    .AddSet<HighlightedParticle>()
    .AddSet<SelectedEntities>()
    // ...
```

## Adding and Removing Entities

### Deferred (default)

Queued during system execution; applied at the next submission. Safe during iteration:

```csharp
World.SetAdd<HighlightedParticle>(particle.EntityIndex);
World.SetRemove<HighlightedParticle>(particle.EntityIndex);
```

### Immediate

`AddImmediate` / `RemoveImmediate` take effect right away. They're thread-safe — usable from the main thread or a job:

```csharp
// Main thread
World.Set<HighlightedParticle>().Write.AddImmediate(entityIndex);
World.Set<HighlightedParticle>().Write.RemoveImmediate(entityIndex);

// In a Burst job, via a NativeSetWrite captured as a job field
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

## Per-Frame Staging

A common pattern is to use a set as a **per-frame scratch list**: clear it at the start of the frame, have one system populate it, then have downstream systems iterate only the members. This avoids recomputing the same predicate in every consumer (rendering, physics sync, audio cues, …).

To make this work *within a single frame*, use the **immediate** APIs (`AddImmediate`, `RemoveImmediate`, `Clear`). Deferred `SetAdd` / `SetRemove` only land at the next submission, so a downstream system in the same frame would see last frame's contents.

```csharp
public partial class CullingSystem : ISystem
{
    public void Execute()
    {
        // Cache the writer once — Set<T>().Write does a sync up front.
        var visible = World.Set<VisibleThisFrame>().Write;

        visible.Clear();

        foreach (var r in Renderable.Query(World).WithTags<GameTags.Renderable>())
        {
            if (Frustum.Intersects(r.Bounds))
                visible.AddImmediate(r.EntityIndex);
        }
    }

    partial struct Renderable : IAspect, IRead<Bounds> { }
}

[ExecuteAfter(typeof(CullingSystem))]
public partial class RenderSystem : ISystem
{
    // Iterates only the entities CullingSystem flagged this frame.
    [ForEachEntity(Set = typeof(VisibleThisFrame))]
    void Render(in MeshInfo mesh, in WorldTransform xform) { ... }
}
```

Notes:

- Sets are **not auto-cleared** between frames. Clear them yourself in the producer system if that's the contract you want.
- Cache the `SetWrite<T>` returned by `Set<T>().Write` outside the loop. Each `.Write` access syncs outstanding job writes; caching syncs once and then writes hit the buffer directly.
- From a Burst job, capture a `NativeSetWrite<T>` as a field for the same `AddImmediate` / `RemoveImmediate` / `Clear` operations with thread-safe writes.

## When to Use Sets vs Tags

| | Tags | Sets |
|---|---|---|
| **Cost of change** | Structural change (deferred, moves data) | Lightweight add/remove from index |
| **Iteration** | All entities with that tag are contiguous in memory | Sparse — only set members are visited |
| **Best for** | Core identity, maximum cache locality | Dynamic membership, temporary flags, filtering |

Both tags (via [partitions](../core/templates.md#partitions)) and sets can represent state, but the trade-offs differ. Tag changes move entity data in memory, giving you dense iteration. Set changes are cheap but iteration is sparse. See [Entity Subset Patterns](../recipes/entity-subset-patterns.md) for a deeper comparison.
