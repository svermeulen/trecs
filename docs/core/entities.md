# Entities

Entities are lightweight identifiers that group components together.

## EntityHandle

`EntityHandle` is the stable reference to an entity. It survives [structural changes](../entity-management/structural-changes.md), so use it whenever you need a long-lived reference (e.g. on a component, on a managed object, or across frames).

You typically obtain a handle from one of these entry points:

```csharp
// At creation time
EntityHandle handle = World.AddEntity<MyTag>()
    .Set(new Position(float3.zero))
    .Handle;

// From inside an aspect query (extension method)
EntityHandle preyHandle = prey.Handle(World);

// From a query iterator
foreach (EntityHandle h in World.Query().WithTags<GameTags.Enemy>().Handles())
{
    // ...
}

// As a [ForEachEntity] parameter
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position pos, EntityHandle handle)
{
    // ...
}
```

## Creating entities

Entities are created via `WorldAccessor.AddEntity()`, which exposes a fluent API for setting component values:

```csharp
World.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42));
```

The tag selects which template to spawn — Trecs looks up the template registered with that identity tag.

The stable handle is available before the next [submission](../entity-management/structural-changes.md):

```csharp
EntityHandle handle = World.AddEntity<MyTag>()
    .Set(new Position(float3.zero))
    .AssertComplete()
    .Handle;
```

Note that `.AssertComplete()` is optional.  This verifies that every non-optional template field has been set. The same check runs at submission; calling it explicitly as part of AddEntity just surfaces the error earlier.

## Removing entities

```csharp
entityHandle.Remove(World);

// Inside an aspect iteration callback, the bound entity removes itself:
entity.Remove();

// Remove every entity matching a tag combination
World.RemoveEntitiesWithTags<SampleTags.Sphere>();
World.RemoveEntitiesWithTags<BallTags.Ball, BallTags.Active>();
```

Removal is **deferred** — the entity disappears at the next [submission](../entity-management/structural-changes.md).

## Accessing entity data

### Aspects (preferred)

Aspects are the primary entity-access API in Trecs — bundled component views with auto-generated read/write properties. Most systems read and mutate entity data through an aspect passed to `[ForEachEntity]`:

```csharp
partial struct FishView : IAspect, IRead<Velocity>, IWrite<Position> { }

[ForEachEntity(typeof(FrenzyTags.Fish))]
void Execute(in FishView fish)
{
    fish.Position.Write.Value += fish.Velocity.Read.Value * World.DeltaTime;
}
```

See [Aspects](../data-access/aspects.md) for the full reference, including manual queries and multi-interface aspects.

### Operating on an entity via its handle

When you have an `EntityHandle` (the stable identifier — see [EntityHandle](#entityhandle) above) you can read components and perform structural changes directly on it, taking the `WorldAccessor` as an argument:

```csharp
// Component access
ref Position pos = ref handle.Component<Position>(World).Write;
ref readonly Velocity vel = ref handle.Component<Velocity>(World).Read;

// Safe access — false if the entity no longer exists or lacks the component
if (handle.TryComponent<Velocity>(World, out var velAccessor))
{
    // ...
}

// Structural / input ops
handle.Remove(World);
handle.SetTag<BallTags.Resting>(World);    // partition transition (turn tag on / switch variant)
handle.UnsetTag<BallTags.Active>(World);   // partition transition (presence/absence dim only)
handle.AddInput<TInput>(World, value);     // queue an input component (only from Input-phase systems)
```

These same methods exist on `EntityIndex` and take the same shape — see the [hot-loop note](#entityindex-hot-loop-variant) below.

You can also receive an `EntityHandle` directly as a `[ForEachEntity]` parameter (typically alongside an aspect for the component data), or from a query terminator:

```csharp
[ForEachEntity(typeof(GameTags.Enemy))]
void Execute(in EnemyView enemy, EntityHandle entity)
{
    if (enemy.Health <= 0) entity.Remove(World);
}

// Single-entity terminator returns an EntityHandle
EntityHandle player = World.Query().WithTags<GameTags.Player>().SingleHandle();

// Multi-entity iterator yields a handle per match
foreach (var e in World.Query().WithTags<GameTags.Enemy>().Handles()) { /* ... */ }
```

### `EntityIndex` (hot-loop variant)

`EntityIndex` is the transient counterpart to `EntityHandle`. It identifies an entity by its buffer position within a specific group, so it's only valid within the current submission cycle — any structural change can invalidate it. In exchange, it skips the per-call handle-to-index lookup that `EntityHandle.X(world)` does internally.

Every entity-targeted method on `EntityHandle` has a matching overload on `EntityIndex`:

```csharp
// Amortize one lookup across multiple ops on the same entity
EntityIndex idx = handle.ToIndex(World);
idx.SetTag<BallTags.Resting>(World);
idx.Component<Position>(World).Write = newPos;

// Or get an EntityIndex directly from a query — no handle ever materialized
foreach (var idx in World.Query().WithTags<GameTags.Active>().Indices()) { /* ... */ }
```

Prefer `EntityHandle` everywhere else — the stability across submissions is almost always worth more than the lookup cost.

## Counting entities

```csharp
int total       = World.CountAllEntities();
int spinners    = World.CountEntitiesWithTags<SampleTags.Spinner>();
int activeBalls = World.CountEntitiesWithTags<BallTags.Ball, BallTags.Active>();
int inGroup     = World.CountEntitiesInGroup(group);
```
