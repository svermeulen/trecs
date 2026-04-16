# Systems

Systems contain the logic that operates on entities. Every system implements `ISystem` and its `Execute()` method is called once per frame at the appropriate update phase.

## Defining a System

```csharp
public partial class SpinnerSystem : ISystem
{
    readonly float _rotationSpeed;

    public SpinnerSystem(float rotationSpeed)
    {
        _rotationSpeed = rotationSpeed;
    }

    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Rotation rotation)
    {
        float angle = World.DeltaTime * _rotationSpeed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

Key points:

- Systems are `partial class` (source generation fills in boilerplate)
- Systems are not created by Trecs. Instantiate them however you like and register with the world builder.
- `World` is a source-generated property providing the `WorldAccessor`

## The Execute Method

Every system must define exactly one method named `Execute`. This is the system's entry point, called once per frame. There are several forms it can take:

- **`[ForEachEntity]` method** — Source-generated iteration over matching entities. This is the most common form.
- **`public void Execute()`** — A manual entry point where you write your own logic, queries, and iteration. Required when you have [multiple `[ForEachEntity]` methods](#multiple-foreachentity-methods) and need to call them explicitly.
- **`[WrapAsJob]` static method** — A `[ForEachEntity]` method that runs as a Burst-compiled parallel job instead of on the main thread. See [Jobs & Burst](../performance/jobs-and-burst.md).

```csharp
// Option 1: ForEachEntity (most common)
public partial class MovementSystem : ISystem
{
    [ForEachEntity(Tag = typeof(GameTags.Player))]
    void Execute(in PlayerView player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }
}

// Option 2: Manual Execute
public partial class SpawnSystem : ISystem
{
    public void Execute()
    {
        if (World.Rng.Next() < 0.1f)
            World.AddEntity<GameTags.Bullet>().Set(new Position(float3.zero));
    }
}

// Option 3: WrapAsJob (parallel, Burst-compiled)
public partial class ParticleMoveSystem : ISystem
{
    [ForEachEntity(Tag = typeof(SampleTags.Particle))]
    [WrapAsJob]
    static void Execute(in Velocity velocity, ref Position position, in NativeWorldAccessor world)
    {
        position.Value += world.DeltaTime * velocity.Value;
    }
}
```

## ForEachEntity

The `[ForEachEntity]` attribute marks a method for source-generated entity iteration. The generator creates the query and loop code automatically.

### Scoping by Tags

```csharp
// Single tag
[ForEachEntity(Tag = typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { ... }

// Multiple tags
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball) { ... }
```

### Scoping by Components

When you don't want to target specific tags, use `MatchByComponents` to iterate all entities that have the required components:

```csharp
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position position, in Velocity velocity)
{
    position.Value += velocity.Value * World.DeltaTime;
}
```

### Scoping by Set

```csharp
[ForEachEntity(Set = typeof(SampleSets.HighlightedParticle))]
void Execute(in ParticleView particle) { ... }
```

### Multiple ForEachEntity Methods

A system can have multiple iteration methods for different entity groups:

```csharp
[VariableUpdate]
public partial class BallRendererSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
    void RenderActive(in ActiveBallView ball)
    {
        // Render active balls as red
    }

    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
    void RenderResting(in RestingBallView ball)
    {
        // Render resting balls as gray
    }

    public void Execute()
    {
        RenderActive();
        RenderResting();
    }

    partial struct ActiveBallView : IAspect, IRead<Position, GameObjectId> { }
    partial struct RestingBallView : IAspect, IRead<Position, GameObjectId> { }
}
```

When you have multiple `[ForEachEntity]` methods, you must provide an explicit `Execute()` that calls them.

### Parameters

`[ForEachEntity]` methods can receive any combination of these parameter types — the source generator wires them automatically:

- **Component refs** — `ref T` (read-write) or `in T` (read-only) for `IEntityComponent` types
- **Aspects** — `in MyAspect` for bundled component access (see [Aspects](../data-access/aspects.md))
- **`EntityIndex`** — the current entity's transient index
- **`EntityHandle`** — the current entity's stable handle
- **`Group`** — the group the current entity belongs to
- **`NativeWorldAccessor`** — job-safe world access (see [Jobs & Burst](../performance/jobs-and-burst.md))
- **`[PassThroughArgument]` parameters** — custom values you pass in when calling the generated method
- **`[GlobalIndex] int`** — a global 0-based index across all matched groups

## SingleEntity

Use `[SingleEntity]` for operations on a singleton entity (asserts exactly one entity matches):

```csharp
[SingleEntity(Tag = typeof(GlobalTag))]
void Execute(ref Score score)
{
    score.Value += 1;
}
```

## Update Phases

Systems run in one of four phases, controlled by attributes:

| Phase | Attribute | Typical Use |
|-------|-----------|-------------|
| Input | `[InputSystem]` | Reading player input |
| Fixed Update | *(default)* | Simulation, physics, game logic |
| Variable Update | `[VariableUpdate]` | Rendering, visual updates |
| Late Variable Update | `[LateVariableUpdate]` | Final frame cleanup |

```csharp
// Fixed update (default — no attribute needed)
public partial class PhysicsSystem : ISystem { ... }

// Variable update
[VariableUpdate]
public partial class RenderSystem : ISystem { ... }

// Input phase
[InputSystem]
public partial class InputSystem : ISystem { ... }
```

Fixed update runs at a fixed timestep (default 1/60s) and may run multiple times per frame to catch up. Variable update runs once per frame at the actual frame rate.

## System Ordering

Control execution order with attributes:

```csharp
[ExecutesAfter(typeof(SpawnSystem))]
public partial class LifetimeSystem : ISystem { ... }

[ExecutesBefore(typeof(RenderSystem))]
public partial class PhysicsSystem : ISystem { ... }

[ExecutePriority(100)]  // Higher priority = runs first
public partial class CriticalSystem : ISystem { ... }
```

Order constraints can also be declared at the builder level:

```csharp
new WorldBuilder()
    .AddSystemOrderConstraint(typeof(SpawnSystem), typeof(LifetimeSystem))
    // ...
```

## Entity Operations in Systems

Systems access the world via the source-generated `World` property:

```csharp
public partial class SpawnSystem : ISystem
{
    public void Execute()
    {
        // Create entities
        World.AddEntity<SampleTags.Sphere>()
            .Set(new Position(float3.zero))
            .Set(new Lifetime(5f));

        // Remove entities
        World.RemoveEntity(entityIndex);

        // Partition transitions
        World.MoveTo<BallTags.Ball, BallTags.Resting>(ball.EntityIndex);

        // Access time and RNG
        float dt = World.DeltaTime;
        float random = World.Rng.Next();
    }
}
```

## Registering Systems

Systems are registered with the world builder:

```csharp
new WorldBuilder()
    .AddSystem(new SpinnerSystem(rotationSpeed: 2f))
    .AddSystem(new SpinnerGameObjectUpdater(gameObjectRegistry))
    .AddSystem(new LifetimeSystem())
    .BuildAndInitialize();
```

Constructor arguments let you inject non-ECS dependencies (registries, configuration, etc.).
