# Groups, GroupIndex & TagSets

This page covers the low-level storage model behind [tags](../core/tags.md). Most users interact with tags through templates and `[ForEachEntity]` attributes and don't need these APIs. They're useful for high performance job scheduling, dynamic queries, or tooling.

It can also be useful to understand conceptually since it's fundamental to how Trecs works.

## Terminology at a Glance

These four terms are related but distinct — the docs and source are careful about which is which:

| Term | What it is | Example |
|---|---|---|
| **Group** | Storage bucket. One per unique tag combination; holds the contiguous component arrays for entities with that exact tag set. | `{Player, VIP}` is a different group from `{Player}`. |
| **Partition** | A group that an entity *moves between* at runtime to represent state, via `SetTag<T>()` / `UnsetTag<T>()` on a tag declared in `IPartitionedBy<…>`. "Partition" is the **role** a group plays — the storage is still a group. | An `Active` ball moves to the `Resting` partition via `ball.UnsetTag<Active>(World)`, without losing its components. |
| **TagSet** | Stable identity for a tag combination — a 32-bit hash of the tag GUIDs. Portable across runs and serializable. | `TagSet<GameTags.Player>.Value` resolves to the same value every run. |
| **Set** | Dynamic, per-frame entity subset — membership is toggled at runtime by code, independent of tags or groups. See [Sets](../entity-management/sets.md). | A `Highlighted` set holding entities currently under the cursor. |

## Groups

A **group** is Trecs' storage unit: a contiguous block of component arrays holding the entities that share one tag combination. Entities tagged `{Player, Character}` live in one group; entities tagged `{Enemy, Character}` live in another.

Groups come from **templates** registered with the world at build time. Each unique tag combination across your registered templates defines exactly one group; the runtime API never creates new groups on the fly.

```csharp
// Two templates registered with WorldBuilder:
//   PlayerEntity : ITagged<Player, Character>
//   EnemyEntity  : ITagged<Enemy, Character>
//
// → Two groups exist: {Player, Character} and {Enemy, Character}.
```

Each group is named by two handles — a stable `TagSet` and a runtime `GroupIndex`. See [TagSet vs GroupIndex](#tagset-vs-groupindex) below.

### Why groups matter

Groups are the foundation of Trecs' performance model:

- **Cache efficiency** — entities with the same tags are packed together, so iteration is fast.
- **Targeted iteration** — systems iterate over specific groups by tag, skipping irrelevant entities.
- **Partitions** — template [partitions](../core/templates.md#partitions) use groups to separate entities by state, so each partition iterates independently.

## `AddEntity`: which group does the entity land in?

The tags you pass to `AddEntity<...>()` are a **filter**, not a label. Trecs picks the registered group whose tag set contains every tag you passed:

- **One group matches** → that's the target.
- **Several match, all from the same registered template** → if there's a unique smallest one whose tag set is a subset of every other match, that's the target. This handles binary partitions, inheritance, and any combination where the matches form a chain inside one template's partition lattice — for example, a template `BallEntity : IExtends<Shape>, ITagged<Ball>, IPartitionedBy<Active>` lets `AddEntity<Ball>()` land in the *absent* partition `{Shape, Ball}` without forcing you to spell out `<Ball, Shape>` everywhere.
- **Matches span multiple templates, or no unique smallest exists** → throws ambiguous.

```csharp
// Given the two templates above ({Player, Character} and {Enemy, Character}):

accessor.AddEntity<GameTags.Player>();
// → {Player, Character}. Only this group contains Player.

accessor.AddEntity<GameTags.Enemy, GameTags.Character>();
// → {Enemy, Character}. Only this group contains Enemy.

accessor.AddEntity<GameTags.Character>();
// → throws. Both groups contain Character, and they belong to different
//   templates — the resolver never picks across template boundaries.
```

`AddEntity<Player>()` works because `Player` narrows to one group. `AddEntity<Character>()` doesn't — `Character` alone matches both `PlayerEntity` and `EnemyEntity`, so you have to add `Player` or `Enemy` to disambiguate.

The cross-template restriction is deliberate: a "forgotten tag" should surface as an error at the call site, not silently route the entity into one template's groups because they happened to be a subset. If your game needs both a base template and a derived template to exist concretely (e.g. `Orc` plus `FlyingOrc`), give each a distinct discriminator tag (`ITagged<Grounded>` on the base, `ITagged<Flying>` on the derived) so their tag sets become siblings rather than strict subsets.

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

`TagSet` is a stable hash, so it's safe to serialize — the same tag combination hashes to the same value across runs.

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
