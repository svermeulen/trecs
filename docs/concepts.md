# Core Concepts

## World

The `World` is the top-level container that manages all ECS state. It owns the entity database, runs systems, and coordinates entity submission.

```csharp
var world = new WorldBuilder()
    .AddTemplate(MyTemplates.Player.Template)
    .AddSystem(new MovementSystem())
    .Build();

world.Initialize();  // Initialize all systems
world.Tick();        // Run fixed + variable update systems
world.LateTick();    // Run late variable update systems
world.Dispose();     // Clean up all resources
```

### WorldBuilder

`WorldBuilder` is the fluent API for configuring a world before construction:

- `AddTemplate(...)` -- Register entity templates (defines which component combinations exist)
- `AddSystem(ISystem)` -- Add a system
- `Build()` -- Construct the world

## Entities

An entity is a lightweight identifier that groups components together. Entities have no behavior of their own -- they are just IDs.

### EntityRef

`EntityRef` is the primary entity handle. It contains a unique ID and a version number, which prevents stale references from accessing recycled entity slots.

```csharp
EntityRef entity = accessor.ScheduleAddEntity<MyTag>();
entity.Set(new MyComponent { Value = 42 });

// Later, after submission:
bool exists = entityRef.Exists();
EntityIndex index = entityRef.ToIndex();  // Convert to index for component access
```

### EntityIndex

`EntityIndex` is a direct index into component arrays for a specific group. It is more efficient than `EntityRef` for component access but is only valid within a single frame (entity indices can change when entities are added or removed).

## Components

Components are unmanaged structs implementing `IEntityComponent`. They hold data only -- no behavior.

```csharp
public struct CPosition : IEntityComponent
{
    public float3 Value;
}

public struct CVelocity : IEntityComponent
{
    public float3 Value;
}
```

### Component Access

Components are accessed through `EcsAccessor` queries:

```csharp
// Query all entities in a group
EntityCollection<CPosition, CVelocity> entities = Ecs.QueryComponents<CPosition, CVelocity>(group);

for (int i = 0; i < entities.Count; i++)
{
    ref var position = ref entities.Buffer1.GetMut(i);
    var velocity = entities.Buffer2[i];
    position.Value += velocity.Value * Ecs.FixedDeltaTime;
}
```

### ComponentRef

For single-entity access:

```csharp
ComponentRef<CPosition> posRef = Ecs.GetComponent<CPosition>(entityIndex);
var pos = posRef.Read;           // Read-only access
ref var pos = ref posRef.Write;  // Write access (marks component as modified)
```

## Tags

Tags are zero-size marker structs implementing `ITag`. They define the "type" of an entity and determine which group it belongs to.

```csharp
public struct Player : ITag { }
public struct Enemy : ITag { }
public struct Projectile : ITag { }
```

Tags are combined into `TagSet` values that identify groups.

## Groups

A `Group` represents a unique combination of tags. All entities with the same set of tags belong to the same group. Groups determine the memory layout -- entities in the same group have their components stored contiguously in memory.

```csharp
// Count entities with specific tags
int playerCount = Ecs.CountEntitiesWithTags<Player>();

// Query components for all entities with a tag combination
foreach (var group in Ecs.GetGroupsWithTags<Player>())
{
    var positions = Ecs.QueryComponents<CPosition>(group);
    // ...
}
```

## Systems

Systems contain game logic. They implement `ISystem` (for managed code) or `IJobSystem` (for Burst-compiled jobs).

```csharp
public partial class MovementSystem : ISystem
{
    [ForEachEntity]
    void Execute(ref CPosition position, in CVelocity velocity)
    {
        position.Value += velocity.Value * Ecs.FixedDeltaTime;
    }
}
```

See [Systems](systems.md) for full details on system types, ordering, and source generation.

## EcsAccessor

`EcsAccessor` is the per-system interface for interacting with the ECS. Each system receives its own accessor with permissions matching its declared dependencies. It provides:

- **Component queries**: `QueryComponents<T>(group)`, `GetComponent<T>(entityIndex)`
- **Entity operations**: `ScheduleAddEntity<T>()`, `ScheduleRemoveEntity(ref)`
- **Counting**: `CountEntitiesWithTags<T>()`, `CountEntitiesInGroup(group)`
- **Timing**: `FixedDeltaTime`, `DeltaTime`, `ElapsedTime`, `Frame`
- **Random**: `FixedRandom`, `VariableRandom` (deterministic RNG streams)
- **Events**: `Events` builder for subscribing to entity lifecycle events
- **Filters**: Entity filter creation and management

### Permission System

Systems declare their component access requirements through `IAccessDeclarations`. The source generator handles this automatically when you use `[ForEachEntity]`, but you can also declare dependencies manually:

```csharp
public void DeclareDependencies(IAccessDeclarations deps)
{
    deps
        .FromEntitiesWithTags<Player>()
        .ReadComponent<CPosition>()
        .WriteComponent<CVelocity>();
}
```

This ensures that conflicting systems cannot access the same components simultaneously.

## Templates

Templates define entity archetypes -- the combination of tags and default component values for a type of entity.

```csharp
public partial class PlayerEntity : ITemplate, ITags<Player>
{
    public CPosition Position;
    public CVelocity Velocity;
    public CHealth Health = new() { Value = 100 };
}
```

See [Templates](templates.md) for more details.
