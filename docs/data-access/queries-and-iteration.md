# Queries & Iteration

Find and iterate over entities by tag, component, or [set](../entity-management/sets.md) membership.

Two entry points:

- **`[ForEachEntity]`** — a method that becomes the body of an auto-generated loop. See [Systems — ForEachEntity](../core/systems.md#foreachentity).
- **`World.Query()`** — a fluent builder for manual `foreach` loops.

## ForEachEntity

```csharp
// By tag
[ForEachEntity(typeof(GameTags.Player))]
void Execute(ref Position position, in Velocity velocity) { ... }

// By multiple tags
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void Execute(in ActiveBall ball) { ... }

// By components (any group whose template declares these components, regardless of tags)
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position position, in Velocity velocity) { ... }

// By set membership
[ForEachEntity(typeof(GameTags.Particle), Set = typeof(SampleSets.Highlighted))]
void Execute(in ParticleView particle) { ... }
```

The named `Tag = typeof(...)` / `Tags = new[] { typeof(...) }` properties also work. The positional form above is the canonical style.

## QueryBuilder

`WorldAccessor.Query()` returns a fluent `QueryBuilder`. Chain filters, then call a terminator.

### Filters

```csharp
// Tags
World.Query().WithTags<GameTags.Player>()
World.Query().WithTags<GameTags.Player, GameTags.Active>()
World.Query().WithoutTags<GameTags.Dead>()

// Components
World.Query().WithComponents<Position, Velocity>()
World.Query().WithoutComponents<Frozen>()
```

`WithTags` / `WithoutTags` and `WithComponents` / `WithoutComponents` come in 1–4-arg generic overloads. Combine them when a tag is shared across templates with different component layouts:

```csharp
// Renderable entities that also have a Velocity component (skips static obstacles).
var query = World.Query()
    .WithTags<CommonTags.Renderable>()
    .WithComponents<Velocity>();
```

A query must have at least one filter — terminators assert otherwise.

### Terminators

```csharp
// Iterate stable handles — primary path; do entity-targeted ops on the handle
foreach (EntityHandle entity in World.Query().WithTags<GameTags.Player>().Handles())
{
    ref Position pos = ref entity.Component<Position>(World).Write;
    pos.Value.y += 1f;
}

// Iterate transient indices — hot-loop variant, avoids the per-iter handle materialization
foreach (EntityIndex idx in World.Query().WithTags<GameTags.Enemy>().Indices())
{
    // ...
}

// Count
int total = World.Query().WithTags<GameTags.Enemy>().Count();

// Single entity (asserts exactly one match) — returns an EntityHandle
EntityHandle player = World.Query().WithTags<GameTags.Player>().SingleHandle();
ref Health hp = ref player.Component<Health>(World).Write;

// Single, no-throw form
if (World.Query().WithTags<GameTags.Player>().TrySingleHandle(out var p))
{
    ref readonly Position pos = ref p.Component<Position>(World).Read;
}
```

Other counting helpers on `WorldAccessor`: `CountAllEntities()`, `CountEntitiesWithTags<T>()`, `CountEntitiesInGroup(GroupIndex)`.

### Sets

`InSet<T>()` filters to members of the given [set](../entity-management/sets.md). Only one set filter per query — test additional sets inside the loop body.

```csharp
foreach (var entity in World.Query()
    .WithTags<GameTags.Particle>()
    .InSet<HighlightedParticles>()
    .Handles())
{
    // ...
}
```

## Aspect queries

Every [aspect](aspects.md) gets a generated `Query()` method that bundles component access into the iteration variable. Read and write through the aspect's properties instead of `....Component<T>(World)`:

```csharp
partial struct PlayerView : IAspect, IRead<Position>, IWrite<Health> { }

foreach (var player in PlayerView.Query(World).WithTags<GameTags.Player>())
{
    float3 pos = player.Position;
    player.Health -= 1f;
}
```

Aspect queries do **not** auto-filter by the aspect's declared components — always scope with `WithTags<…>()`, `MatchByComponents()`, or `InSet<…>()`.


```csharp
// Match by the aspect's component shape, regardless of tags.
foreach (var boid in Boid.Query(World).MatchByComponents()) { ... }
```

`PlayerView.Query(World).WithTags<GameTags.Player>().Single()` works as well.

## GlobalIndex

When iteration spans multiple groups, `[GlobalIndex] int` gives each entity a unique packed index from `0` to `total − 1`. Useful for filling a contiguous output array shared across groups:

```csharp
[ForEachEntity(typeof(GameTags.Boid))]
void Execute(in CPosition pos, [GlobalIndex] int globalIndex)
{
    _outputs[globalIndex] = pos.Value;
}
```

## GroupSlices

For performance-critical loops needing direct buffer access, iterate per group via `GroupSlices()` rather than per entity. See [GroupSlices](../advanced/groups-and-tagsets.md#groupslices).

## Where queries are allowed

The accessor's [role](../advanced/accessor-roles.md) determines which groups a query can resolve — Fixed-role accessors can't iterate `[VariableUpdateOnly]`-only groups, for example. The query asserts at the terminator if it would otherwise return groups the accessor isn't permitted to read.
