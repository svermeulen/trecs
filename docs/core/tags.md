# Tags

Tags are zero-cost markers that classify entities. They carry no data — they exist purely to categorize entities for templates, systems, and queries.

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

Tags are declared on templates via `ITagged`:

```csharp
public partial class SpinnerEntity : ITemplate, ITagged<SampleTags.Spinner>
{
    Rotation Rotation;
}
```

See [Templates](templates.md) for details on partitions (`IHasPartition`) and template inheritance.

## Tags in Systems

Systems target specific tag combinations to iterate only matching entities:

```csharp
// Iterate only entities with the Spinner tag
[ForEachEntity(typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { ... }

// Iterate entities with both Ball and Active tags
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
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
int count = World.CountEntitiesWithTags<GameTags.Player>();
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

## How Tags Affect Storage

Behind the scenes, entities with the same tag combination are stored together in contiguous memory for cache-friendly iteration. Iterating all entities with a given tag is fast — they're packed together, and unrelated entities are skipped entirely.

For the underlying storage model and the low-level `TagSet`, `GroupIndex`, and `Tag<T>` APIs, see [Groups, GroupIndex & TagSets](../advanced/groups-and-tagsets.md).
