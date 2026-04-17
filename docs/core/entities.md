# Entities

Entities are lightweight identifiers that group components together

## EntityHandle vs EntityIndex

Trecs provides two ways of referring to entities for different use cases:

`EntityHandle` is a stable reference that survives [structural changes](../entity-management/structural-changes.md) and can be stored in components for cross-entity relationships. `EntityIndex` is a fast, transient reference for immediate use within a system tick — it provides direct buffer access but is invalidated when entities are added, removed, or moved.

| | EntityHandle | EntityIndex |
|---|---|---|
| **Stability** | Stable across structural changes | Invalidated by structural changes |
| **Use case** | Long-lived references (store in components) | Immediate access within a system tick |
| **Fields** | `UniqueId`, `Version` | `Index`, `Group` |
| **Performance** | Requires lookup to access components | Direct buffer access |

Note that you can always convert between the two as needed:

```csharp
// EntityHandle — stable reference
EntityHandle handle = index.ToHandle(world);

// EntityIndex — fast but transient
EntityIndex index = handle.ToIndex(world);
```

## Creating Entities

Entities are created via `WorldAccessor.AddEntity()`, which returns an `EntityInitializer` for setting component values:

```csharp
// Specify tag, which should map to a unique entity type
world.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42));
```

### EntityInitializer

The initializer is a `ref struct` — it must be used immediately, not stored:

```csharp
var initializer = world.AddEntity<MyTag>();
initializer.Set(new Position(float3.zero));
initializer.Set(new Velocity(float3.zero));
```

EntityInitializer exposes a `Handle` property which provides the entity's stable reference:

```csharp
var init = world.AddEntity<MyTag>();
EntityHandle handle = init.Handle;  // Available immediately
init.Set(new Position(float3.zero));
```

!!! tip
    You can optionally call `AssertComplete()` on the initializer to verify that all non-optional components declared by the template have been set. This check also runs automatically during entity submission, so `AssertComplete()` is only useful for catching mistakes earlier at the call site.  Note that non-optional components are all components declared on the template definition (ITemplate) without an explicit value.

## Removing Entities

```csharp
// Remove a single entity
world.RemoveEntity(entityIndex);
world.RemoveEntity(entityHandle);

// Remove all entities with specific tags
world.RemoveEntitiesWithTags<SampleTags.Sphere>();
world.RemoveEntitiesWithTags<BallTags.Ball, BallTags.Active>();
```

!!! note
    Entity removal is deferred — the entity is not immediately destroyed. It is removed during the next entity submission phase. See [Structural Changes](../entity-management/structural-changes.md).

## Accessing Entity Data

Use `EntityAccessor` for convenient component access on a single entity:

```csharp
// From EntityIndex
var entity = index.ToEntity(world);
ref Position pos = ref entity.Get<Position>().Write;

// From EntityHandle
var entity = handle.ToEntity(world);
ref readonly Velocity vel = ref entity.Get<Velocity>().Read;

// Safe access
if (entity.TryGet<Velocity>(out var velAccessor))
{
    // Entity has Velocity component
}
```

However note that in many cases using the [aspects](../data-access/aspects.md) feature is better practice

## Counting Entities

```csharp
int total = world.CountAllEntities();
int spinners = world.CountEntitiesWithTags<SampleTags.Spinner>();
int activeBalls = world.CountEntitiesWithTags<BallTags.Ball, BallTags.Active>();
int inGroup = world.CountEntitiesInGroup(group);
```
