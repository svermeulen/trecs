# Entities

Entities are lightweight identifiers that group components together.

## EntityHandle vs EntityIndex

Trecs has two ways of referring to entities:

- **`EntityHandle`** — a stable reference that survives [structural changes](../entity-management/structural-changes.md). Use it whenever you need to store a long-lived pointer to another entity (e.g. on a component or on a managed object).
- **`EntityIndex`** — a fast, transient reference that points directly into the underlying buffers. It's invalidated by any structural change, so it's only safe within the current frame.

Convert between them:

```csharp
// EntityIndex → EntityHandle (stable)
EntityHandle handle = index.ToHandle(World);

// EntityHandle → EntityIndex (transient)
EntityIndex index = handle.ToIndex(World);
```

`World` is the system's source-generated `WorldAccessor` property — see [Systems](systems.md).

## Creating entities

Entities are created via `WorldAccessor.AddEntity()`, which returns an `EntityInitializer` for setting component values:

```csharp
World.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42));
```

The tag selects which template to spawn — Trecs looks up the template registered with that identity tag.

`EntityInitializer` is a `ref struct`. Use it immediately; don't store it across method boundaries. The stable handle is available before the next [submission](../entity-management/structural-changes.md):

```csharp
var init = World.AddEntity<MyTag>();
EntityHandle handle = init.Handle;
init.Set(new Position(float3.zero));
```

Calling `init.AssertComplete()` verifies that every non-optional field on the template has been set. The same check runs automatically during submission; an explicit call just surfaces the error earlier, at the call site.

## Removing entities

```csharp
World.RemoveEntity(entityIndex);
World.RemoveEntity(entityHandle);

// Remove every entity matching a tag combination
World.RemoveEntitiesWithTags<SampleTags.Sphere>();
World.RemoveEntitiesWithTags<BallTags.Ball, BallTags.Active>();
```

Removal is **deferred** — the entity disappears at the next [submission](../entity-management/structural-changes.md).

## Accessing entity data

`EntityAccessor` is a convenient single-entity component view:

```csharp
var entity = index.ToEntity(World);
ref Position pos = ref entity.Get<Position>().Write;

// Or from a handle
var entity2 = handle.ToEntity(World);
ref readonly Velocity vel = ref entity2.Get<Velocity>().Read;

// Safe access
if (entity.TryGet<Velocity>(out var velAccessor))
{
    // ...
}
```

For most cases prefer [aspects](../data-access/aspects.md) — they bundle related components into a single typed view with auto-generated read/write properties.

## Counting entities

```csharp
int total       = World.CountAllEntities();
int spinners    = World.CountEntitiesWithTags<SampleTags.Spinner>();
int activeBalls = World.CountEntitiesWithTags<BallTags.Ball, BallTags.Active>();
int inGroup     = World.CountEntitiesInGroup(group);
```
