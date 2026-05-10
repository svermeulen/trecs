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

All set mutations go through the gateway `World.Set<T>()`, which exposes three timing modes via its properties:

| Property | Timing | When applied |
|---|---|---|
| `.Defer` | Submission-deferred | Next call to `SubmitEntities()` |
| `.Write` | Synchronous | Immediately (main thread, syncs outstanding jobs) |
| `.Read`  | Synchronous read | Immediately (main thread, syncs outstanding writers) |

### Deferred (default)

Queued during system execution; applied at the next submission. Safe during iteration:

```csharp
World.Set<HighlightedParticle>().Defer.Add(particle.Handle(World));
World.Set<HighlightedParticle>().Defer.Remove(particle.Handle(World));
World.Set<HighlightedParticle>().Defer.Clear();
```

A queued `Defer.Clear()` **supersedes** any `Defer.Add` / `Defer.Remove` queued for the same set in the same submission, regardless of call order.  If you want sequential semantics within a single frame ("clear, then add these"), use the immediate APIs below.

### Immediate

`Set<T>().Write` returns a synced view; its `Add` / `Remove` / `Clear` take effect right away. The sync runs once at acquisition, so cache the view for tight loops.

```csharp
// Main thread
var highlighted = World.Set<HighlightedParticle>().Write;
highlighted.Add(handle);
highlighted.Remove(handle);
highlighted.Clear();

// In a Burst job, via a NativeSetCommandBuffer captured as a job field
highlighted.Add(handle, world);
highlighted.Remove(handle, world);
```

!!! warning "Don't mutate a set while iterating it"
    Using an immediate `Add` / `Remove` / `Clear` on the **same set in the same group** you're currently iterating throws in DEBUG builds. In release builds (where the assertion is compiled out) iteration corrupts silently — entries get skipped, revisited, or (when an `Add` grows the buffer) read from freed memory.

    Everything else is safe: mutating a different set, mutating the same set in a different group, or using the deferred API (`Set<T>().Defer`) on the iterated set (it applies at the next submission).

    To mutate a set you're iterating, prefer the deferred APIs — or stage the changes in a `NativeList<EntityHandle>` and apply them after the loop.

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

A common pattern: use a set as a **per-frame scratch list**. One system clears and populates it; downstream systems iterate only the members. This avoids recomputing the same predicate against many entities in every consumer (rendering, physics sync, audio cues, etc.).

To make this work *within a single frame*, use the **immediate** APIs. Deferred set ops only land at the next submission, so a downstream system in the same frame would see last frame's contents.

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
                visible.Add(r.Handle(World));
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
- From a Burst job, capture a `NativeSetCommandBuffer<T>` as a field for thread-safe `Add` / `Remove` / `Clear`. Job-side `Clear` wipes the set's pre-existing contents and supersedes any `Add` / `Remove` queued in the same writer-job-cycle, regardless of call order — analogous to the deferred-clear semantics on `Set<T>().Defer.Clear()`.

## Sets vs tags

| | Tags | Sets |
|---|---|---|
| **Cost of change** | Structural change (deferred, moves data) | Lightweight add/remove from index |
| **Iteration** | All entities with that tag are contiguous in memory | Sparse — only set members are visited |
| **Best for** | Core identity, maximum cache locality | Dynamic membership, temporary flags, filtering |

Both tags (via [partitions](../core/templates.md#partitions)) and sets can represent state, but the trade-offs differ. Tag changes move entity data in memory, giving you dense iteration. Set changes are cheap but iteration is sparse. See [Entity Subset Patterns](../guides/entity-subset-patterns.md) for a deeper comparison.

## See also

- [Sample 08 — Sets](../samples/08-sets.md): a complete example of producer/consumer set membership across systems.
