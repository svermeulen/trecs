# Groups, GroupIndex & TagSets

This page covers the low-level storage model behind [tags](../core/tags.md). Most users interact with tags through templates and `[ForEachEntity]` attributes and don't need these APIs. They're useful for high performance job scheduling, dynamic queries, or tooling.

It can also be useful to understand conceptually since it's fundamental to how Trecs works.

## Terminology at a Glance

These four terms are related but distinct — the docs and source are careful about which is which:

| Term | What it is | Example |
|---|---|---|
| **Group** | Storage bucket. One per unique tag combination; holds the contiguous component arrays for entities with that exact tag set. | `{Player, VIP}` is a different group from `{Player}`. |
| **Partition** | A group that an entity *moves between* at runtime to represent state, via `MoveTo<FromTag, ToTag>()`. "Partition" is the **role** a group plays — the storage is still a group. | An `Active` ball moves to the `Resting` partition without losing its components. |
| **TagSet** | Stable identity for a tag combination — a 32-bit hash of the tag GUIDs. Portable across runs and serializable. | `TagSet<GameTags.Player>.Value` resolves to the same value every run. |
| **Set** | Dynamic, per-frame entity subset — membership is toggled at runtime by code, independent of tags or groups. See [Sets](../entity-management/sets.md). | A `Highlighted` set holding entities currently under the cursor. |

## Groups

Groups come from **templates** registered with the world at build time. Each template's tag combination defines one group; you never create groups directly through the runtime API.

```csharp
// Two templates registered with WorldBuilder:
//   PlayerEntity : ITagged<Player>, ITagged<Active>
//   EnemyEntity  : ITagged<Enemy>, ITagged<Active>
//
// → Two groups exist: {Player, Active} and {Enemy, Active}.
```

The tags passed to `AddEntity` are **not** the group's full tag set — they're a query that resolves to the unique group whose tags contain them as a subset. When several groups match the subset, the lookup prefers one whose tag set is exactly equal to the query (this is what lets you target the "empty" side of a presence/absence partition by omitting the partition tag). Otherwise it throws.

```csharp
accessor.AddEntity<GameTags.Player>();
// → {Player, Active} — only one group contains Player

accessor.AddEntity<GameTags.Enemy, GameTags.Active>();
// → {Enemy, Active} — exact match

accessor.AddEntity<GameTags.Active>();
// → throws: both groups contain Active (ambiguous)
```

Entities in the same group share the same component layout and are stored contiguously.

### Why groups matter

Groups are the foundation of Trecs' performance model:

- **Cache efficiency** — entities with the same tags are packed together, so iteration is fast
- **Targeted iteration** — systems iterate over specific groups by tag, skipping irrelevant entities
- **Partitions** — template [partitions](../core/templates.md#partitions) use groups to separate entities by state, so each partition iterates independently

## TagSet vs GroupIndex

Trecs exposes two first-class handles for groups:

| | `TagSet` | `GroupIndex` |
|---|---|---|
| **Role** | Stable identity for a tag combination | Runtime handle — a small array-indexable integer |
| **Representation** | 32-bit stable hash of the tag GUIDs | Sequential `ushort` assigned at world build time |
| **Serializable** | Yes — same value across runs | No — assignment depends on registration order |
| **Typical use** | `[FromWorld(typeof(GameTags.Player))]`, `world.CountEntitiesWithTags<...>()`, save-game fields | `[ForEachEntity]` internals, group-slice iteration, `ComponentBuffer`, event callbacks |
| **How you get one** | `TagSet<GameTags.Player>.Value`, `TagSet.FromTags(...)` | `worldInfo.GetSingleGroupWithTags(tagSet)`, capture from slice, event callback |

Rule of thumb: to **store** the handle (component, disk, across sessions), use `TagSet`. To **use** it within a frame to reach into native storage, use `GroupIndex`.

## GroupSlices

`GroupSlices()` is a low-level iteration pattern that gives direct access to component buffers per group. It bypasses the per-entity abstraction and can be more efficient for bulk operations, but requires you to manage group-level access yourself.

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

`ComponentBuffer<T>(group)` returns a `ComponentBufferAccessor<T>` — use `.Read` or `.Write` to get the native buffer (`NativeComponentBufferRead<T>` / `NativeComponentBufferWrite<T>`). The choice registers the access with the [dependency tracker](../performance/dependency-tracking.md).

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
    Prefer `[ForEachEntity]`, aspect queries, or `.Entities()` / `.EntityHandles()` iteration. GroupSlices are useful when you need maximum throughput and are comfortable working at the group level. See [Queries & Iteration](../data-access/queries-and-iteration.md) for the higher-level alternatives.

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

`TagSet` is a stable hash, so it's safe to serialize — the same tag combination hashes to the same value across runs. Use it for save-game fields or network messages that name a group.

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

`GroupIndex` values are assigned sequentially during `WorldBuilder.Build()` and are stable for the lifetime of a `World`. They are **not** stable across runs — don't store them on disk. Persist the `TagSet` and resolve back to a `GroupIndex` after load.

Event callbacks receive `GroupIndex` directly when iterating ranges:

```csharp
accessor.Events.EntitiesWithTags<GameTags.Enemy>()
    .OnRemoved((GroupIndex group, EntityRange indices) => { ... });
```

## `Tag<T>`

For runtime tag operations, use the `Tag<T>` cache:

```csharp
Tag playerTag = Tag<GameTags.Player>.Value;
```
