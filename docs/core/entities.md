# Entities

Entities are lightweight identifiers that group components together.

## EntityHandle vs EntityIndex

Trecs has two ways of referring to entities, each suited to different use cases:

- **`EntityHandle`** — a stable reference that survives [structural changes](../entity-management/structural-changes.md). Store it in components or fields when you need a long-lived link to another entity.
- **`EntityIndex`** — a fast, transient reference that points directly at the underlying component buffers. It's the right choice inside a single system tick but is invalidated as soon as entities are added, removed, or moved between groups.

| | EntityHandle | EntityIndex |
|---|---|---|
| **Stability** | Stable across structural changes | Invalidated by structural changes |
| **Use case** | Long-lived references (store in components) | Immediate access within a system tick |
| **Fields** | `UniqueId`, `Version` | `Index`, `GroupIndex` |
| **Performance** | Requires lookup to access components | Direct buffer access |

Convert between them as needed:

```csharp
// EntityIndex → EntityHandle (stable)
EntityHandle handle = index.ToHandle(World);

// EntityHandle → EntityIndex (transient)
EntityIndex index = handle.ToIndex(World);
```

`World` here is the system's source-generated `WorldAccessor` property — see [Systems](systems.md).

## Creating Entities

Entities are created via `WorldAccessor.AddEntity()`, which returns an `EntityInitializer` for setting component values:

```csharp
// The tag identifies which template (and therefore which components) to spawn
World.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42));
```

### EntityInitializer

`EntityInitializer` is a `ref struct` — use it immediately, do not store it across method boundaries:

```csharp
var initializer = World.AddEntity<MyTag>();
initializer.Set(new Position(float3.zero));
initializer.Set(new Velocity(float3.zero));
```

The entity's stable handle is available right away via the `Handle` field, even though the entity itself only materializes during the next [submission](../entity-management/structural-changes.md):

```csharp
var init = World.AddEntity<MyTag>();
EntityHandle handle = init.Handle;
init.Set(new Position(float3.zero));
```

!!! tip
    Call `AssertComplete()` on the initializer to verify that every non-optional component on the template has been set. The check also runs automatically during submission; explicit calls just surface the error earlier, at the call site. A "non-optional" component is a template field declared without a default value.

## Removing Entities

```csharp
// Remove a single entity
World.RemoveEntity(entityIndex);
World.RemoveEntity(entityHandle);

// Remove all entities matching a tag combination
World.RemoveEntitiesWithTags<SampleTags.Sphere>();
World.RemoveEntitiesWithTags<BallTags.Ball, BallTags.Active>();
```

!!! note
    Entity removal is **deferred** — the entity is not destroyed immediately. It disappears during the next [submission](../entity-management/structural-changes.md).

## Accessing Entity Data

`EntityAccessor` is a convenient single-entity component view:

```csharp
// From EntityIndex
var entity = index.ToEntity(World);
ref Position pos = ref entity.Get<Position>().Write;

// From EntityHandle
var entity = handle.ToEntity(World);
ref readonly Velocity vel = ref entity.Get<Velocity>().Read;

// Safe access
if (entity.TryGet<Velocity>(out var velAccessor))
{
    // Entity has Velocity component
}
```

For batch processing, prefer [aspects](../data-access/aspects.md) — they bundle related components into a single typed view.

## Counting Entities

```csharp
int total       = World.CountAllEntities();
int spinners    = World.CountEntitiesWithTags<SampleTags.Spinner>();
int activeBalls = World.CountEntitiesWithTags<BallTags.Ball, BallTags.Active>();
int inGroup     = World.CountEntitiesInGroup(group);
```
