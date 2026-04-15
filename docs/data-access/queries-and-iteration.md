# Queries & Iteration

Queries let you find and iterate over entities by their tags, components, or set membership. Most of the time you'll use `[ForEachEntity]` (see [Systems](../core/systems.md)), but manual queries give you full control.

## QueryBuilder

Access the query builder via `WorldAccessor.Query()`:

```csharp
var query = World.Query()
    .WithTags<GameTags.Player>()
    .WithComponents<Health>();
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

### Filtering by Set

Calling `InSet<T>()` transitions to a `SparseQueryBuilder` — the iteration will only visit entities that are members of the set:

```csharp
var sparseQuery = World.Query()
    .WithTags<GameTags.Particle>()
    .InSet<HighlightedParticles>();
```

## Iteration Patterns

### EntityIndices — Iterate Entity by Entity

```csharp
var iter = World.Query().WithTags<GameTags.Player>().EntityIndices();
while (iter.MoveNext())
{
    EntityIndex entityIndex = iter.Current;
    ref Position pos = ref World.Component<Position>(entityIndex).Write;
    pos.Value.y += 1f;
}
```

### GroupSlices — Iterate per Group (Dense)

More efficient for bulk operations since you can access component buffers directly:

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

### Sparse GroupSlices — Iterate Set Members

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

## Aspect-Based Queries

Aspects provide a generated `Query()` method for manual iteration with type-safe component access:

```csharp
partial struct PlayerView : IAspect, IRead<Position>, IWrite<Health> { }

foreach (var player in PlayerView.Query(World).WithTags<GameTags.Player>())
{
    float3 pos = player.Position;
    player.Health -= 1f;
}
```

See [Aspects](aspects.md) for details.
