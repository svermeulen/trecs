# Queries & Iteration

Queries let you find and iterate over entities by their tags, components, or set membership.

## ForEachEntity

The most common way to iterate entities is with `[ForEachEntity]` on a system method. It supports the same filtering criteria as the query builder:

```csharp
// By tag
[ForEachEntity(Tag = typeof(GameTags.Player))]
void Execute(ref Position position, in Velocity velocity) { ... }

// By multiple tags
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball) { ... }

// By components (matches any entity that has these components, regardless of tags)
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position position, in Velocity velocity) { ... }

// By set membership
[ForEachEntity(Set = typeof(SampleSets.HighlightedParticle))]
void Execute(in ParticleView particle) { ... }
```

See [Systems](../core/systems.md#foreachentity) for full details.

## QueryBuilder

For manual iteration outside of `[ForEachEntity]`, use the query builder via `WorldAccessor.Query()`:

```csharp
var query = World.Query().WithTags<GameTags.Player>()
```

### Filtering by Tags

```csharp
// Include entities with specific tags
query.WithTags<GameTags.Player>()
query.WithTags<GameTags.Player, GameTags.Active>()

// Exclude entities with specific tags
query.WithoutTags<GameTags.Dead>()
```

### Filtering by Components

```csharp
// Include entities that have specific components
query.WithComponents<Position, Velocity>()

// Exclude entities that have specific components
query.WithoutComponents<Frozen>()
```

### Filtering by both Components and Tags

Tag and component filters can be combined. This is useful when a tag is shared across multiple entity types that have different component layouts, and you only want to iterate entities that have specific components:

```csharp
// Only iterate Renderable entities that also have a Velocity component
var query = World.Query()
    .WithTags<CommonTags.Renderable>()
    .WithComponents<Velocity>();
```

For example, if both `FishEntity` and `ObstacleEntity` share a `Renderable` tag but only `FishEntity` has a `Velocity` component, this query will skip obstacles and only visit fish.

## Iteration Patterns

### EntityIndices — Iterate Entity by Entity

```csharp
foreach (EntityIndex entityIndex in World.Query().WithTags<GameTags.Player>().EntityIndices())
{
    ref Position pos = ref World.Component<Position>(entityIndex).Write;
    pos.Value.y += 1f;
}
```

### Aspect Queries

Aspects provide a generated `Query()` method for manual iteration with bundled component access:

```csharp
partial struct PlayerView : IAspect, IRead<Position>, IWrite<Health> { }

foreach (var player in PlayerView.Query(World).WithTags<GameTags.Player>())
{
    float3 pos = player.Position;
    player.Health -= 1f;
}
```

!!! note
    Aspect queries do **not** automatically filter by the aspect's components — you must provide filtering criteria. Either scope with `WithTags` or call `MatchByComponents()` to filter to only groups that have all the aspect's components:

    ```csharp
    // Iterates any entity that has Position and Health, regardless of tags
    foreach (var entity in PlayerView.Query(World).MatchByComponents())
    {
        ...
    }
    ```

See [Aspects](aspects.md) for details on defining aspects.

### Filtering by Set

Calling `InSet<T>()` filters iteration to only entities that are members of the [set](../entity-management/sets.md):

```csharp
partial struct ParticleView : IAspect, IRead<Position>, IWrite<ColorComponent> { }

foreach (var particle in ParticleView.Query(World).InSet<HighlightedParticles>())
{
    particle.Color = Color.yellow;
}
```

### GroupSlices — Low-Level Bulk Access

For advanced performance-critical scenarios, you can iterate per-group and access component buffers directly. See [Groups & TagSets — GroupSlices](../advanced/groups-and-tagsets.md#groupslices) for details.

## Counting

```csharp
int total = World.CountAllEntities();
int players = World.CountEntitiesWithTags<GameTags.Player>();
int inGroup = World.CountEntitiesInGroup(group);
int matched = World.Query().WithTags<GameTags.Enemy>().Count();
```

## Single Entity Access

For queries that should match exactly one entity:

```csharp
// Assert exactly one match
EntityAccessor player = World.Query().WithTags<GameTags.Player>().Single();
ref Health hp = ref player.Get<Health>().Write;

// Try-pattern (returns false if 0 or 2+ matches)
if (World.Query().WithTags<GameTags.Player>().TrySingle(out var player))
{
    ref readonly Position pos = ref player.Get<Position>().Read;
}
```
