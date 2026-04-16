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

## Tag&lt;T&gt; and Zero-Allocation Access

For runtime tag operations, use the `Tag<T>` cache to avoid allocations:

```csharp
Tag playerTag = Tag<GameTags.Player>.Value;       // Cached, zero-allocation
int nativeGuid = Tag<GameTags.Player>.NativeGuid;  // Burst-compatible
```
