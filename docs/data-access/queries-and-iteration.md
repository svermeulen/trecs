# Queries & Iteration

Find and iterate over entities by tag, component, or [set](../entity-management/sets.md) membership.

Two entry points:

- **`[ForEachEntity]`** — A method that will serve as the body of the loop over matching entities. See [Systems — ForEachEntity](../core/systems.md#foreachentity).
- **`World.Query()`** — A manual fluent query builder that can be used with `foreach` loops

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

The named `Tag = typeof(...)` / `Tags = new[] { typeof(...) }` properties also work. The constructor-positional form shown above is the canonical style.

## QueryBuilder

`WorldAccessor.Query()` returns a fluent `QueryBuilder`. Chain filters, then call a terminator.

### Filtering

```csharp
// Tags
World.Query().WithTags<GameTags.Player>()
World.Query().WithTags<GameTags.Player, GameTags.Active>()
World.Query().WithoutTags<GameTags.Dead>()

// Components
World.Query().WithComponents<Position, Velocity>()
World.Query().WithoutComponents<Frozen>()
```

`WithTags` / `WithoutTags` and `WithComponents` / `WithoutComponents` come in 1–4-arg generic overloads. Tag and component filters can be combined — useful when a tag is shared across templates with different component layouts:

```csharp
// Renderable entities that also have a Velocity component (skips static obstacles).
var query = World.Query()
    .WithTags<CommonTags.Renderable>()
    .WithComponents<Velocity>();
```

A query must have at least one filter — terminators assert otherwise.

### Terminators

```csharp
// Iterate entity-by-entity
foreach (EntityIndex entityIndex in World.Query().WithTags<GameTags.Player>().EntityIndices())
{
    ref Position pos = ref World.Component<Position>(entityIndex).Write;
    pos.Value.y += 1f;
}

// Count
int total = World.Query().WithTags<GameTags.Enemy>().Count();

// Single entity (asserts exactly one match)
EntityAccessor player = World.Query().WithTags<GameTags.Player>().Single();
ref Health hp = ref player.Get<Health>().Write;

// Single, no-throw form
if (World.Query().WithTags<GameTags.Player>().TrySingle(out var player))
{
    ref readonly Position pos = ref player.Get<Position>().Read;
}
```

Other counting helpers on `WorldAccessor`: `CountAllEntities()`, `CountEntitiesWithTags<T>()`, `CountEntitiesInGroup(GroupIndex)`.

### Sets

`InSet<T>()` filters to entities that are members of the given [set](../entity-management/sets.md). It returns a `SparseQueryBuilder` (set-filtered iteration is fundamentally sparse) — only one set can be applied per query.  To match on extra sets, test for set membership within the loop body.

```csharp
foreach (var entityIndex in World.Query()
    .WithTags<GameTags.Particle>()
    .InSet<HighlightedParticles>()
    .EntityIndices())
{
    ...
}
```

## Aspect queries

Every [aspect](aspects.md) gets a generated `Query()` method that bundles component access into the iteration variable. Unlike the entity-index form, you read and write through the aspect's properties rather than calling `World.Component<T>(...)`.

```csharp
partial struct PlayerView : IAspect, IRead<Position>, IWrite<Health> { }

foreach (var player in PlayerView.Query(World).WithTags<GameTags.Player>())
{
    float3 pos = player.Position;
    player.Health -= 1f;
}
```

!!! note
    Aspect queries do **not** auto-filter by the aspect's declared components. Always scope with `WithTags<…>()`, `MatchByComponents()`, or `InSet<…>()`. Without one of these, the query has no group scope and asserts.

```csharp
// Match all entities that have the aspect's declared components, regardless of tags.
foreach (var boid in Boid.Query(World).MatchByComponents()) { ... }
```

`PlayerView.Query(World).WithTags<GameTags.Player>().Single()` works as well.

## GlobalIndex

When iteration spans multiple groups, `[GlobalIndex] int` gives each entity a unique packed index from `0` to `total − 1`. Useful for writing into a contiguous output array shared across groups.

```csharp
[ForEachEntity(typeof(GameTags.Boid))]
void Execute(in CPosition pos, [GlobalIndex] int globalIndex)
{
    _outputs[globalIndex] = pos.Value;
}
```

The parameter must be `int`. Job-side only — works in manual job structs and in `[WrapAsJob]`-generated jobs. See also [Advanced Job Features](../advanced/advanced-jobs.md).

## GroupSlices

For performance-critical loops that need direct buffer access, iterate per group via `GroupSlices()` rather than per entity. See [Groups, GroupIndex & TagSets — GroupSlices](../advanced/groups-and-tagsets.md#groupslices).

## Where queries are allowed

The accessor's [role](../advanced/accessor-roles.md) determines which groups a query can resolve — Fixed-role accessors can't iterate `[VariableUpdateOnly]`-only groups, for example. The query asserts at the terminator if it would otherwise return groups the accessor isn't permitted to read.
