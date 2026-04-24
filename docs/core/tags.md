# Tags

Tags are zero-cost markers that classify entities. They carry no data — they exist purely to categorize entities for use in templates, systems, and queries.

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

## Tags in Templates

Tags are declared on templates via `IHasTags`:

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<SampleTags.Spinner>
{
    public Rotation Rotation;
}
```

See [Templates](templates.md) for details on partitions (`IHasPartition`) and template inheritance.

## Tags in Systems

Systems target specific tag combinations to iterate only matching entities:

```csharp
// Iterate only entities with the Spinner tag
[ForEachEntity(Tag = typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { ... }

// Iterate entities with both Ball and Active tags
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball) { ... }
```

Tags can also be used with `World.Query()` for manual iteration:

```csharp
// Iterate with an aspect
foreach (var player in PlayerView.Query(World).WithTags<GameTags.Player>())
{
    player.Position += player.Velocity * World.DeltaTime;
}

// Get a single entity
var boss = BossView.Query(World).WithTags<GameTags.Boss>().Single();
```

See [Queries & Iteration](../data-access/queries-and-iteration.md) for the full query API.

## Tags in Queries

```csharp
int count = world.CountEntitiesWithTags<GameTags.Player>();
world.RemoveEntitiesWithTags<GameTags.Bullet>();
```

## How Tags Affect Storage

Behind the scenes, entities with the same tag combination are stored together in contiguous memory for cache-friendly iteration. This means that iterating all entities with a given tag is fast — they are packed together, and unrelated entities are skipped entirely.

For more on the underlying storage model and low-level APIs like `TagSet`, `GroupIndex`, and `Tag<T>`, see [Groups, GroupIndex & TagSets](../advanced/groups-and-tagsets.md).
