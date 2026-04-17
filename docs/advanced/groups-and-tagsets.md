# Groups & TagSets

This page covers the low-level storage model behind [tags](../core/tags.md). Most users interact with tags through templates and `[ForEachEntity]` attributes and don't need the APIs described here. These are useful for advanced scenarios like custom job scheduling, dynamic queries, or tooling.

## Groups

Groups are created **implicitly** from tag combinations. You never create groups directly — they emerge from how you use tags in templates and `AddEntity` calls.

```csharp
// These two entities belong to different groups:
world.AddEntity<GameTags.Player>();                // Group: {Player}
world.AddEntity<GameTags.Player, GameTags.VIP>();  // Group: {Player, VIP}
```

Each unique tag combination maps to exactly one group. Entities in the same group share the same component layout and are stored contiguously in memory.

### Why Groups Matter

Groups are the foundation of Trecs' performance model:

- **Cache efficiency** — entities with the same tags are packed together, so iterating over them is fast
- **Targeted iteration** — systems can iterate over specific groups by tag, skipping irrelevant entities entirely
- **Partitions** — template [partitions](../core/templates.md#partitions) use groups to separate entities by state, so each partition can be iterated independently

## GroupSlices

`GroupSlices()` is a low-level iteration pattern that gives you direct access to component buffers per group. This bypasses the per-entity abstraction and can be more efficient for bulk operations, but requires you to manage group-level access yourself.

### Dense GroupSlices

```csharp
foreach (var slice in World.Query().WithTags<GameTags.Player>().GroupSlices())
{
    var positions = World.ComponentBuffer<Position>(slice.Group);
    var velocities = World.ComponentBuffer<Velocity>(slice.Group);

    for (int i = 0; i < slice.Count; i++)
    {
        positions[i].Value += velocities[i].Value * dt;
    }
}
```

### Sparse GroupSlices (Set Members)

When querying with `InSet<T>()`, iteration is sparse — only set members are visited:

```csharp
foreach (var slice in World.Query().InSet<HighlightedParticles>().GroupSlices())
{
    var colors = World.ComponentBuffer<ColorComponent>(slice.Group);

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

## Tag&lt;T&gt;

For runtime tag operations, use the `Tag<T>` cache:

```csharp
Tag playerTag = Tag<GameTags.Player>.Value;
```
