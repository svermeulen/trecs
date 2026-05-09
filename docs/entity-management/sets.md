# Sets

A set is a lightweight membership flag for entities — an entity is either in the set or it isn't. Sets are independent of an entity's components and tags, and iteration visits only the members.

## Defining a set

```csharp
public struct HighlightedParticle : IEntitySet { }
```

To restrict membership to entities carrying specific tags, use the generic form. Adding an entity without those tags asserts:

```csharp
public struct EatingFish : IEntitySet<FrenzyTags.Fish> { }
```

## Registering sets

```csharp
new WorldBuilder()
    .AddSet<HighlightedParticle>()
    .AddSet<SelectedEntities>()
    // ...
```

## Adding, removing, and clearing

### Deferred (default)

Queued during system execution; applied at the next submission. Safe during iteration:

```csharp
World.SetAdd<HighlightedParticle>(particle.EntityIndex);
World.SetRemove<HighlightedParticle>(particle.EntityIndex);
World.SetClear<HighlightedParticle>();
```

A queued `SetClear<T>` **supersedes** any `SetAdd<T>` / `SetRemove<T>` queued for the same set in the same submission, regardless of call order — analogous to how a queued remove supersedes a queued move. If you want sequential semantics within a single frame ("clear, then add these"), use the immediate APIs below.

### Immediate

`AddImmediate` / `RemoveImmediate` / `ClearImmediate` take effect right away. `AddImmediate` and `RemoveImmediate` are thread-safe — usable from the main thread or a job. `ClearImmediate` is main-thread only.

```csharp
// Main thread
World.Set<HighlightedParticle>().Write.AddImmediate(entityIndex);
World.Set<HighlightedParticle>().Write.RemoveImmediate(entityIndex);
World.Set<HighlightedParticle>().Write.ClearImmediate();

// In a Burst job, via a NativeSetWrite captured as a job field
highlighted.AddImmediate(entityIndex);
highlighted.RemoveImmediate(entityIndex);
```

!!! warning "Don't mutate a set while iterating it"
    `AddImmediate`, `RemoveImmediate`, and `ClearImmediate` modify the set's storage in place. Calling them on the same set + same group you're currently iterating corrupts iteration — entries get skipped, revisited, or (when an `Add` grows the underlying buffer) read from freed memory.

    | Op during iteration of set `S`, group `G` | Same set + same group | Same set, different group | Different set |
    |---|---|---|---|
    | `AddImmediate` / `RemoveImmediate` / `ClearImmediate` | **Unsafe** | Safe | Safe |
    | Deferred `SetAdd` / `SetRemove` / `SetClear` | Safe (applied at next submission) | Safe | Safe |

    DEBUG builds throw at the point of misuse. Release builds corrupt silently.

    To mutate a set you're iterating, prefer the deferred APIs — or stage the changes in a `List<EntityIndex>` and apply them after the loop.

## Querying by set

```csharp
// ForEachEntity
[ForEachEntity(Set = typeof(HighlightedParticle))]
void Execute(in ParticleView particle) { /* only visits set members */ }

// Aspect query
foreach (var particle in ParticleView.Query(World).InSet<HighlightedParticle>())
{
    particle.Color = Color.yellow;
}

// Count
int highlighted = World.Query().InSet<HighlightedParticle>().Count();
```

## Per-frame staging

A common pattern: use a set as a **per-frame scratch list**. One system clears and populates it; downstream systems iterate only the members. This avoids recomputing the same predicate in every consumer (rendering, physics sync, audio cues, etc.).

To make this work *within a single frame*, use the **immediate** APIs. Deferred set ops only land at the next submission, so a downstream system in the same frame would see last frame's contents.

```csharp
public partial class CullingSystem : ISystem
{
    public void Execute()
    {
        // Cache the writer once — Set<T>().Write does a sync up front.
        var visible = World.Set<VisibleThisFrame>().Write;

        visible.ClearImmediate();

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
    [ForEachEntity(Set = typeof(VisibleThisFrame))]
    void Render(in MeshInfo mesh, in WorldTransform xform) { ... }
}
```

Notes:

- Sets are **not auto-cleared** between frames. Clear them yourself in the producer system if that's the contract you want.
- Cache the `SetWrite<T>` returned by `Set<T>().Write` outside the loop. Each `.Write` access syncs outstanding job writes; caching syncs once and then writes hit the buffer directly.
- From a Burst job, capture a `NativeSetWrite<T>` as a field for thread-safe `AddImmediate` / `RemoveImmediate`. Clearing from inside a job isn't supported — call `World.SetClear<T>()` (deferred) before the job dispatches, or `ClearImmediate` on the main thread.

## Sets vs tags

| | Tags | Sets |
|---|---|---|
| **Cost of change** | Structural change (deferred, moves data) | Lightweight add/remove from index |
| **Iteration** | All entities with that tag are contiguous in memory | Sparse — only set members are visited |
| **Best for** | Core identity, maximum cache locality | Dynamic membership, temporary flags, filtering |

Both tags (via [partitions](../core/templates.md#partitions)) and sets can represent state, but the trade-offs differ. Tag changes move entity data in memory, giving you dense iteration. Set changes are cheap but iteration is sparse. See [Entity Subset Patterns](../recipes/entity-subset-patterns.md) for a deeper comparison.

## See also

- [Sample 08 — Sets](../samples/08-sets.md): a worked example of producer/consumer set membership across systems.
