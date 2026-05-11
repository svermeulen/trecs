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
foreach (EntityHandle h in World.Query().WithTags<GameTags.Enemy>().EntityHandles())
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

`.AssertComplete()` verifies that every non-optional template field has been set. The same check runs at submission; calling it explicitly surfaces the error earlier, at the call site.

## Removing entities

```csharp
World.RemoveEntity(entityHandle);

// Inside an iteration callback, the bound entity removes itself:
entity.Remove();

// Remove every entity matching a tag combination
World.RemoveEntitiesWithTags<SampleTags.Sphere>();
World.RemoveEntitiesWithTags<BallTags.Ball, BallTags.Active>();
```

Removal is **deferred** — the entity disappears at the next [submission](../entity-management/structural-changes.md).

## Accessing entity data

`EntityAccessor` is a live single-entity view bound to a `WorldAccessor`. It exposes component access plus remove and partition-transition operations on the bound entity.

```csharp
EntityAccessor entity = World.Entity(handle);

ref Position pos = ref entity.Get<Position>().Write;
ref readonly Velocity vel = ref entity.Get<Velocity>().Read;

// Safe access
if (entity.TryGet<Velocity>(out var velAccessor))
{
    // ...
}

// Structural / input ops on the bound entity
entity.Remove();
entity.SetTag<BallTags.Resting>();      // partition transition (turn tag on / switch variant)
entity.UnsetTag<BallTags.Active>();     // partition transition (presence/absence dim only)

// Resolve the stable handle when you need to store it
EntityHandle handle = entity.Handle;
```

`EntityAccessor` is a `ref struct` — it lives on the stack and isn't suitable for storage. For long-lived references, store an `EntityHandle` instead.

You can also receive an `EntityAccessor` directly as a `[ForEachEntity]` parameter, or from a query terminator:

```csharp
[ForEachEntity(typeof(GameTags.Enemy))]
void Execute(in EnemyView enemy, EntityAccessor entity)
{
    if (enemy.Health <= 0) entity.Remove();
}

// Single-entity terminator returns an EntityAccessor
EntityAccessor player = World.Query().WithTags<GameTags.Player>().Single();

// Multi-entity terminator yields one per match
foreach (var e in World.Query().WithTags<GameTags.Enemy>().Entities()) { /* ... */ }
```

For most multi-component access prefer [aspects](../data-access/aspects.md) — they bundle related components into a typed view with auto-generated read/write properties.

## Counting entities

```csharp
int total       = World.CountAllEntities();
int spinners    = World.CountEntitiesWithTags<SampleTags.Spinner>();
int activeBalls = World.CountEntitiesWithTags<BallTags.Ball, BallTags.Active>();
int inGroup     = World.CountEntitiesInGroup(group);
```
