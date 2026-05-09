# Groups, GroupIndex & TagSets

This page covers the low-level storage model behind [tags](../core/tags.md). Most users interact with tags through templates and `[ForEachEntity]` attributes and don't need the APIs described here. These are useful for advanced scenarios like custom job scheduling, dynamic queries, or tooling.

## Terminology at a Glance

These four terms are related but distinct — the docs and source are careful about which is which:

| Term | What it is | Example |
|---|---|---|
| **Group** | Concrete storage bucket. One per unique tag combination; holds the contiguous component arrays for entities with that exact tag set. | `{Player, VIP}` is a different group from `{Player}`. |
| **Partition** | A group that an entity *moves between* at runtime to represent state, via `MoveTo<FromTag, ToTag>()`. "Partition" is the **role** a group plays in that pattern — the storage is still just a group. | An `Active` ball moves to the `Resting` partition without losing its components. |
| **TagSet** | Stable identity for a tag combination — a 32-bit hash of the tag GUIDs. Portable across runs and serializable. | `TagSet<GameTags.Player>.Value` resolves to the same value every run. |
| **Set** | Dynamic, per-frame entity subset — membership is toggled at runtime by code, independent of tags or groups. See [Sets](../entity-management/sets.md). | A `Highlighted` set that holds whichever entities are currently under the cursor. |

Quick mental model: **tags** describe identity at creation; **groups** are where those tags place the entity in memory; **partitions** are groups that double as state; **tagsets** are portable handles naming a tag combination; **sets** are ad-hoc subsets you update yourself.

## Groups

Groups are created **implicitly** from tag combinations. You never create groups directly — they emerge from how you use tags in templates and `AddEntity` calls.

```csharp
// These two entities belong to different groups:
accessor.AddEntity<GameTags.Player>();                // group: {Player}
accessor.AddEntity<GameTags.Player, GameTags.VIP>();  // group: {Player, VIP}
```

Each unique tag combination maps to exactly one group. Entities in the same group share the same component layout and are stored contiguously in memory.

### Why groups matter

Groups are the foundation of Trecs' performance model:

- **Cache efficiency** — entities with the same tags are packed together, so iterating over them is fast
- **Targeted iteration** — systems can iterate over specific groups by tag, skipping irrelevant entities entirely
- **Partitions** — template [partitions](../core/templates.md#partitions) use groups to separate entities by state, so each partition can be iterated independently

## TagSet vs GroupIndex

Trecs exposes two first-class handles for groups, with a clear layering:

| | `TagSet` | `GroupIndex` |
|---|---|---|
| **Role** | Stable identity for a tag combination | Runtime handle — a small array-indexable integer |
| **Representation** | 32-bit stable hash of the tag GUIDs | Sequential `ushort` assigned at world build time |
| **Serializable** | Yes — same value across runs | No — assignment depends on registration order |
| **Typical use** | `[FromWorld(typeof(GameTags.Player))]`, `world.CountEntitiesWithTags<...>()`, save-game fields | `[ForEachEntity]` internals, group-slice iteration, `ComponentBuffer`, event callbacks |
| **How you get one** | `TagSet<GameTags.Player>.Value`, `TagSet.FromTags(...)` | `worldInfo.GetSingleGroupWithTags(tagSet)`, capture from slice, event callback |

Rule of thumb: if you're going to **store** the handle (in a component, on disk, across sessions), use `TagSet`. If you're **using** it within a frame to reach into native storage, use `GroupIndex`.

## GroupSlices

`GroupSlices()` is a low-level iteration pattern that gives you direct access to component buffers per group. This bypasses the per-entity abstraction and can be more efficient for bulk operations, but requires you to manage group-level access yourself.

### Dense GroupSlices

```csharp
foreach (var slice in accessor.Query().WithTags<GameTags.Player>().GroupSlices())
{
    var positions = accessor.ComponentBuffer<Position>(slice.GroupIndex).Write;
    var velocities = accessor.ComponentBuffer<Velocity>(slice.GroupIndex).Read;

    for (int i = 0; i < slice.Count; i++)
    {
        positions[i].Value += velocities[i].Value * dt;
    }
}
```

`ComponentBuffer<T>(group)` returns a `ComponentBufferAccessor<T>` — use its `.Read` or `.Write` property to get the concrete native buffer (`NativeComponentBufferRead<T>` / `NativeComponentBufferWrite<T>`). The choice is what registers the access with the [dependency tracker](../performance/dependency-tracking.md).

### Sparse GroupSlices (set members)

When querying with `InSet<T>()`, iteration is sparse — only set members are visited:

```csharp
foreach (var slice in accessor.Query().InSet<HighlightedParticles>().GroupSlices())
{
    var colors = accessor.ComponentBuffer<ColorComponent>(slice.GroupIndex).Write;

    foreach (int idx in slice.Indices)
    {
        colors[idx].Value = Color.yellow;
    }
}
```

!!! tip
    For most use cases, prefer `[ForEachEntity]`, aspect queries, or `EntityIndices()` iteration. GroupSlices are mainly useful when you need maximum throughput and are comfortable working at the group level. See [Queries & Iteration](../data-access/queries-and-iteration.md) for the higher-level alternatives.

## TagSet

A `TagSet` is an immutable combination of tags. Tag sets are cached and compared by ID for fast equality checks.

```csharp
// Generic cache (zero-allocation)
TagSet playerTags = TagSet<GameTags.Player>.Value;
TagSet playerBullet = TagSet<GameTags.Player, GameTags.Bullet>.Value;

// From Tag values
TagSet tags = TagSet.FromTags(Tag<GameTags.Player>.Value, Tag<GameTags.Enemy>.Value);

// Combining tag sets
TagSet combined = playerTags.CombineWith(TagSet<GameTags.Active>.Value);
```

Because `TagSet` is a stable hash, it's safe to serialize — the same tag combination always hashes to the same value across runs. Use it for save-game fields or network messages that name a group.

## GroupIndex

`GroupIndex` is the runtime handle the core ECS plumbing uses to index into per-group storage (component buffers, sets, events). It's a `ushort` internally, 1-based with a `GroupIndex.Null` sentinel.

```csharp
// Resolve a TagSet to its GroupIndex (main-thread world queries)
GroupIndex playerGroup = worldInfo.GetSingleGroupWithTags(TagSet<GameTags.Player>.Value);

// Null check
if (playerGroup.IsNull) { /* no group registered for this tag combination */ }

// Use with per-group APIs (on a WorldAccessor)
var positions = accessor.ComponentBuffer<Position>(playerGroup).Read;
int count = accessor.CountEntitiesInGroup(playerGroup);
```

`GroupIndex` values are assigned sequentially during `WorldBuilder.Build()` and are stable for the lifetime of a `World`. They are **not** stable across runs — don't store them on disk. Persist the `TagSet` instead and resolve back to a `GroupIndex` after load.

`EntityIndex` carries a `GroupIndex` directly:

```csharp
EntityIndex idx = handle.ToIndex(accessor);
GroupIndex group = idx.GroupIndex;
int indexInGroup = idx.Index;
```

Event callbacks also receive `GroupIndex`:

```csharp
accessor.Events.EntitiesWithTags<GameTags.Enemy>()
    .OnRemoved((GroupIndex group, EntityRange indices) => { ... });
```

## Tag&lt;T&gt;

For runtime tag operations, use the `Tag<T>` cache:

```csharp
Tag playerTag = Tag<GameTags.Player>.Value;
```
