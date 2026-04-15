# Systems

Systems contain the game logic that operates on entities and their components.

## System Interfaces

### ISystem

The standard system interface for managed code:

```csharp
public partial class MySystem : ISystem
{
    // Called once after the world is initialized
    public void Initialize() { }

    // Called by the source generator -- you typically use [ForEachEntity] instead
    public void Execute() { }

    // Access the ECS via the Ecs property (EcsAccessor)
    // Ecs.FixedDeltaTime, Ecs.QueryComponents<T>(), etc.
}
```

### IJobSystem

For Burst-compiled parallel jobs:

```csharp
public partial class MyJobSystem : IJobSystem
{
    public void Initialize() { }

    public void Execute(JobHandle inputDeps)
    {
        // Schedule Unity jobs and return the combined handle
    }
}
```

## Source-Generated Iteration

### [ForEachEntity]

The most common pattern. Mark a method with `[ForEachEntity]` and the source generator creates the iteration code, dependency declarations, and group matching:

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

**Parameter conventions:**
- `ref T` -- Read/write access to component `T`
- `in T` / `T` -- Read-only access to component `T`
- The method name can be anything, but `Execute` is conventional

The generator iterates over all groups that contain the referenced components and calls your method for each entity.

### [ForEachAspect]

For more complex iteration patterns, define an aspect -- a named group of components with declared read/write access -- and iterate over it:

```csharp
[Aspect]
partial struct MovementView : IRead<CVelocity>, IWrite<CPosition>
{
    // Optional: restrict to specific tags
    public static readonly TagSet Tags = TagSet<Player>.Value;
}

public partial class MySystem : ISystem
{
    [ForEachAspect]
    void Execute(in MovementView view)
    {
        view.Position.Value += view.Velocity.Value * Ecs.FixedDeltaTime;
    }
}
```

## Update Phases

Systems run in one of several update phases:

### Fixed Update (default)

Systems run at a fixed timestep (deterministic). This is the default phase. Use `Ecs.FixedDeltaTime` for timing.

### Variable Update

Systems run once per frame. Apply the `[VariableUpdate]` attribute:

```csharp
[VariableUpdate]
public partial class RenderSyncSystem : ISystem
{
    [ForEachEntity]
    void Execute(in CPosition position)
    {
        // Runs once per frame, use Ecs.DeltaTime
    }
}
```

### Late Variable Update

Runs after variable update, in `World.LateTick()`. Apply `[LateVariableUpdate]`:

```csharp
[LateVariableUpdate]
public partial class CameraFollowSystem : ISystem
{
    // Runs in LateTick()
}
```

### Input System

For processing input before the main update loop. Apply `[InputSystem]`:

```csharp
[InputSystem]
public partial class InputProcessorSystem : ISystem
{
    // Runs during input processing phase
}
```

## System Ordering

Control the execution order of systems within a phase:

### [ExecutesAfter] / [ExecutesBefore]

```csharp
[ExecutesAfter(typeof(PhysicsSystem))]
public partial class CollisionResponseSystem : ISystem { }

[ExecutesBefore(typeof(RenderSystem))]
public partial class AnimationSystem : ISystem { }
```

### [ExecutePriority]

Set an explicit numeric priority (lower runs first):

```csharp
[ExecutePriority(10)]
public partial class EarlySystem : ISystem { }

[ExecutePriority(100)]
public partial class LateSystem : ISystem { }
```

## Dependency Declaration

When not using `[ForEachEntity]` (which auto-generates dependencies), declare them manually:

```csharp
public void DeclareDependencies(IAccessDeclarations deps)
{
    deps
        .FromEntitiesWithTags<Player>()
        .ReadComponent<CPosition>()
        .WriteComponent<CVelocity>();

    deps
        .FromEntitiesWithTags<Enemy>()
        .ReadComponent<CPosition>();
}
```

This tells the world which components your system reads and writes, enabling validation and potential future parallelization.

## Entity Operations in Systems

### Creating Entities

```csharp
var entity = Ecs.ScheduleAddEntity<MyTag>();
entity.Set(new CPosition { Value = float3.zero });
// Entity is created on next SubmitEntities()
```

### Removing Entities

```csharp
Ecs.ScheduleRemoveEntity(entityRef);
// Entity is removed on next SubmitEntities()
```

### Accessing the Global Entity

The global entity is a singleton entity for world-wide state:

```csharp
var globalHealth = Ecs.GetGlobalComponent<CWorldState>();
```

## Events

Subscribe to entity lifecycle events:

```csharp
Ecs.Events
    .OnEntityAdded<MyTag>((ref CPosition pos) =>
    {
        // Called when an entity with MyTag is added
    })
    .OnEntityRemoved<MyTag>((ref CPosition pos) =>
    {
        // Called when an entity with MyTag is about to be removed
    });
```
