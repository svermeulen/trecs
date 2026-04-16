# Entities

Entities are lightweight identifiers that group components together

## EntityHandle vs EntityIndex

Trecs provides two ways of referring to entities for different use cases:

`EntityHandle` is a stable reference that can be stored long-term, while `EntityIndex` is a fast, transient reference for immediate use within a system tick.

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
// Create with a single tag
world.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42))
    .AssertComplete();

// Create with multiple tags
world.AddEntity<BallTags.Ball, BallTags.Active>()
    .Set(new Position(float3.zero))
    .Set(new Velocity(new float3(0, 5, 0)))
    .AssertComplete();
```

### EntityInitializer

The initializer is a `ref struct` — it must be used immediately, not stored:

```csharp
var initializer = world.AddEntity<MyTag>();
initializer.Set(new Position(float3.zero));
initializer.Set(new Velocity(float3.zero));
initializer.AssertComplete();  // Validates all required components are set
```

The `Handle` property provides the entity's stable reference:

```csharp
var init = world.AddEntity<MyTag>();
EntityHandle handle = init.Handle;  // Available immediately
init.Set(new Position(float3.zero))
    .AssertComplete();
```

!!! warning
    `AssertComplete()` verifies that all components declared by the entity's template have been initialized. Always call it to catch missing component initialization at development time.

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

## Handle/Index Conversion

```csharp
// Handle → Index
EntityIndex index = handle.ToIndex(world);

// Index → Handle
EntityHandle handle = index.ToHandle(world);

// Safe conversion (returns false if entity no longer exists)
if (handle.TryToIndex(world, out EntityIndex index))
{
    // Entity still alive
}

// Check existence
if (handle.Exists(world))
{
    // Entity still alive
}
```

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
