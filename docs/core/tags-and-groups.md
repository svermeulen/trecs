# Tags & Groups

Tags are zero-cost markers that classify entities. Entities with the same tag combination are stored together in a **group** — a contiguous memory layout optimized for cache-friendly iteration.

!!! tip
    Most of the time, you interact with groups indirectly through tags. Direct group access is a low-level API useful for advanced scenarios like custom job scheduling, heavy optimization, or tooling.

## Defining Tags

Tags are empty structs implementing `ITag`:

```csharp
public static class GameTags
{
    public struct Player : ITag { }
    public struct Enemy : ITag { }
    public struct Bullet : ITag { }
}
```

Tags carry no data — they exist purely to categorize entities and determine their group membership, for use in queries.

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

## Groups

Groups are created **implicitly** from tag combinations. You never create groups directly — they emerge from how you use tags in templates and `AddEntity` calls.

```csharp
// These two entities belong to different groups:
world.AddEntity<GameTags.Player>();           // Group: {Player}
world.AddEntity<GameTags.Player, GameTags.VIP>(); // Group: {Player, VIP}
```

Each unique tag combination maps to exactly one group. Entities in the same group share the same component layout and are stored contiguously in memory.

### Why Groups Matter

Groups are the foundation of Trecs' performance model:

- **Cache efficiency** — entities with the same tags are packed together, so iterating over them is fast
- **Targeted iteration** — systems can iterate over specific groups by tag, skipping irrelevant entities entirely
- **Partitions** — template partitions use groups to separate entities by partition, so each partition can be iterated independently

## Tags in Templates

Tags are declared on templates via `IHasTags`:

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<SampleTags.Spinner>
{
    public Rotation Rotation;
}
```

See [Templates](templates.md) for details on partitions (`IHasPartition`) and how tags define entity partitions.

## Tags in Systems

Systems can target specific tag combinations:

```csharp
// Iterate only entities with the Spinner tag
[ForEachEntity(Tag = typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { ... }

// Iterate entities with both Ball and Active tags
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball) { ... }
```

## Tags in Queries

```csharp
int count = world.CountEntitiesWithTags<GameTags.Player>();
world.RemoveEntitiesWithTags<GameTags.Bullet>();
```

## Tag<T> and Zero-Allocation Access

For runtime tag operations, use the `Tag<T>` cache to avoid allocations:

```csharp
Tag playerTag = Tag<GameTags.Player>.Value;     // Cached, zero-allocation
int nativeGuid = Tag<GameTags.Player>.NativeGuid; // Burst-compatible
```
