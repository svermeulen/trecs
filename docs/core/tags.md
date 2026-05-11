# Tags

Tags are zero-cost markers that classify entities. They carry no data — they categorize entities for templates, systems, and queries.

## Defining tags

Tags are empty structs implementing `ITag`:

```csharp
public static class GameTags
{
    public struct Player : ITag { }
    public struct Enemy : ITag { }
    public struct Bullet : ITag { }
}
```

## Tags on templates

Tags are declared via `ITagged`:

```csharp
public partial class SpinnerEntity : ITemplate, ITagged<SampleTags.Spinner>
{
    Rotation Rotation;
}
```

See [Templates](templates.md) for partitions (`IPartitionedBy`) and template inheritance.

## Tags in systems

Systems target tag combinations to iterate only matching entities:

```csharp
[ForEachEntity(typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { /* ... */ }

[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void Execute(in ActiveBall ball) { /* ... */ }
```

Tags also drive manual iteration. `World.Query()` and aspect queries both accept tag filters:

```csharp
// Aspect query — bundled component access through the aspect's properties.
foreach (var player in PlayerView.Query(World).WithTags<GameTags.Player>())
{
    player.Position += player.Velocity * World.DeltaTime;
}

// World.Query() — returns an EntityAccessor; useful when no aspect fits.
var boss = World.Query().WithTags<GameTags.Boss>().Single();
```

See [Queries & Iteration](../data-access/queries-and-iteration.md) for the full filter / terminator API.

## Tags in queries

```csharp
int count = World.CountEntitiesWithTags<GameTags.Player>();
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

## Storage

Entities with the same tag combination are stored together in contiguous memory for cache-friendly iteration. Unrelated entities are skipped entirely.

For the storage model and the low-level `TagSet` / `GroupIndex` / `Tag<T>` APIs, see [Groups, GroupIndex & TagSets](../advanced/groups-and-tagsets.md).
