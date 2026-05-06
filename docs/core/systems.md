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
- `World` is a source-generated **instance property** providing the `WorldAccessor` — not the `World` class. It only exists inside types the source generator processes (systems, event handlers, and `[ForEachEntity]` hosts). In a plain helper class you need to inject a `WorldAccessor` explicitly (see [World Setup — WorldAccessor](world-setup.md#worldaccessor)).

## The Execute Method

Every system must define exactly one method named `Execute`. This is the system's entry point, called once per frame. There are several forms it can take:

- **`[ForEachEntity]` method** — Source-generated iteration over matching entities. This is the most common form.
- **`public void Execute()`** — A manual entry point where you write your own logic, queries, and iteration. Required when you have [multiple `[ForEachEntity]` methods](#multiple-foreachentity-methods) and need to call them explicitly.
- **`[WrapAsJob]` static method** — A `[ForEachEntity]` method that runs as a Burst-compiled parallel job instead of on the main thread. See [Jobs & Burst](../performance/jobs-and-burst.md).

```csharp
// Option 1: ForEachEntity (most common)
public partial class MovementSystem : ISystem
{
    [ForEachEntity(typeof(GameTags.Player))]
    void Execute(in PlayerView player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }
}

// Option 2: Manual Execute
public partial class DamageSystem : ISystem
{
    public void Execute()
    {
        foreach (var enemy in EnemyView.Query(World).WithTags<GameTags.Enemy>())
        {
            if (enemy.Health <= 0)
                World.RemoveEntity(enemy.EntityIndex);
        }
    }

    partial struct EnemyView : IAspect, IRead<Health>, IAspectEntityIndex { }
}

// Option 3: WrapAsJob (parallel, Burst-compiled)
public partial class ParticleMoveSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Particle))]
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
[ForEachEntity(typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { ... }

// Multiple tags
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
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

See [Sets](../entity-management/sets.md) for more on defining and using sets.

### Multiple ForEachEntity Methods

A system can have multiple iteration methods for different entity groups:

```csharp
[Phase(SystemPhase.Presentation)]
public partial class BallRendererSystem : ISystem
{
    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
    void RenderActive(in ActiveBallView ball)
    {
        // Render active balls as red
    }

    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Resting))]
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

`[ForEachEntity]` methods accept the following parameter types. The source generator wires them automatically.

**Entity data** (choose one style per method — cannot be mixed):

- **Component refs** — `ref T` (read-write) or `in T` (read-only) for `IEntityComponent` types. Multiple components can be listed.
- **Aspect** — `in MyAspect` for bundled component access (see [Aspects](../data-access/aspects.md)). Only one aspect per method.

**Additional parameters** (can be combined with either style above):

- **`EntityIndex`** — the current entity's transient index
- **`WorldAccessor`** — the system's world accessor (main-thread methods only)
- **`NativeWorldAccessor`** — job-safe world access (`[WrapAsJob]` methods only). See [Jobs & Burst](../performance/jobs-and-burst.md).
- **`[PassThroughArgument]`** — custom values passed in by the caller. See [below](#passthroughargument).

### PassThroughArgument

Mark a parameter with `[PassThroughArgument]` to pass custom values into a `[ForEachEntity]` method. The generated method will include matching parameters that the caller must provide:

```csharp
public partial class ParticleBoundSystem : ISystem
{
    readonly float _halfSize;

    [ForEachEntity(typeof(SampleTags.Particle))]
    [WrapAsJob]
    static void ExecuteAsJob(
        ref Velocity velocity,
        ref Position position,
        [PassThroughArgument] float halfSize
    )
    {
        // halfSize is passed in by the caller, not looked up from the world
        if (position.Value.x > halfSize || position.Value.x < -halfSize)
            velocity.Value.x = -velocity.Value.x;
    }

    public void Execute()
    {
        // Pass _halfSize to the generated method
        ExecuteAsJob(_halfSize);
    }
}
```

This is useful for passing configuration, precomputed values, or other data that isn't a component on the iterated entities. `[PassThroughArgument]` works with both main-thread and `[WrapAsJob]` methods, but the value must be an unmanaged type when used with jobs.

## SingleEntity

`[SingleEntity]` marks an individual parameter (or a job-struct field) that should be resolved to the unique entity matching the given tag(s). The framework runs the equivalent of `World.Query().WithTags<...>().Single()` before the user method body, asserts exactly one match, and binds the result to the parameter or field.

Inline tags are mandatory — `[SingleEntity]` has no runtime override and no `Optional` mode. If you need a runtime-supplied query, call `World.Query().WithTags(...).Single()` directly inside the method body.

```csharp
void Execute([SingleEntity(typeof(GlobalTag))] ref Score score)
{
    score.Value += 1;
}
```

`[SingleEntity]` works in four contexts:

**Plain `Execute` methods.** A method whose only iteration markers are per-parameter `[SingleEntity]` runs *once* per call; the framework hoists every singleton lookup before the body.

```csharp
void Execute(
    [SingleEntity(typeof(PlayerTag))] in PlayerView player,
    [SingleEntity(typeof(GlobalsTag))] in GlobalsView globals)
{
    // ...
}
```

**Mixed with `[ForEachEntity]`.** A method that already iterates can also receive singleton parameters; the lookup is hoisted out of the loop and bound once per call.

```csharp
[ForEachEntity(Tag = typeof(EnemyTag))]
void Process(in EnemyView enemy, [SingleEntity(typeof(PlayerTag))] in PlayerView player)
{
    // 'player' is resolved once before the loop and reused for every enemy.
}
```

**`[WrapAsJob]` static methods.** Singleton parameters become job-struct fields that the generator wires up at schedule time, so the body remains Burst-clean.

```csharp
[ForEachEntity(Tag = typeof(EnemyTag))]
[WrapAsJob]
static void Process(
    in EnemyView enemy,
    [SingleEntity(typeof(PlayerTag))] in PlayerView player)
{
    // ...
}
```

**Hand-written job-struct fields.** Adding `[SingleEntity]` directly on a field of an `IJobFor` (or other custom job struct) makes the generator populate it at schedule time, the same way `[FromWorld]` populates other field kinds.

```csharp
public partial struct ScoringJob : IJobFor
{
    [SingleEntity(typeof(GlobalTag))]
    public NativeWrite<Score> Score;
    // ...
}
```

## Update Phases

Systems run in one of five phases, controlled by `[Phase(...)]`. Phases execute in this order each rendered frame:

| Phase | Attribute | Typical Use |
|-------|-----------|-------------|
| `EarlyPresentation` | `[Phase(SystemPhase.EarlyPresentation)]` | Variable-cadence sampling that needs to feed into the fixed loop (e.g. raw mouse delta accumulation) |
| `Input` | `[Phase(SystemPhase.Input)]` | Reading player input — runs just-in-time before each fixed step (0..N times per frame) |
| `Fixed` | *(default)* | Deterministic simulation, physics, game logic |
| `Presentation` | `[Phase(SystemPhase.Presentation)]` | Rendering, transform sync, interpolation reads |
| `LatePresentation` | `[Phase(SystemPhase.LatePresentation)]` | Post-animation corrections — runs in Unity's `LateUpdate` |

```csharp
// Fixed (default — no attribute needed)
public partial class PhysicsSystem : ISystem { ... }

// Presentation
[Phase(SystemPhase.Presentation)]
public partial class RenderSystem : ISystem { ... }

// Input
[Phase(SystemPhase.Input)]
public partial class KeyboardInputSystem : ISystem { ... }
```

The fixed phase runs at a fixed timestep (default 1/60s) and may run multiple times per frame to catch up (or zero times at fast variable frame rates). Each fixed step is preceded by the input phase. Presentation and LatePresentation run once per rendered frame. See [Input System](../advanced/input-system.md) for details on the input phase.

Trecs does not hook into Unity's update loop automatically — you drive it by calling these methods on the world each frame:

- **`world.Tick()`** — Runs `EarlyPresentation`, the input/fixed loop, and `Presentation` phases.
- **`world.LateTick()`** — Runs the `LatePresentation` phase. Call from `MonoBehaviour.LateUpdate`.

Typically these are called from a MonoBehaviour like this:

```csharp
public class GameLoop : MonoBehaviour
{
    World _world;

    void Start()
    {
        _world = new WorldBuilder()
            .AddEntityType(PlayerEntity.Template)
            .AddSystem(new MovementSystem())
            .BuildAndInitialize();
    }

    void Update()
    {
        _world.Tick();
    }

    void LateUpdate()
    {
        _world.LateTick();
    }

    void OnDestroy()
    {
        _world.Dispose();
    }
}
```

All outstanding jobs are completed at the boundary between phases. See [Dependency Tracking](../performance/dependency-tracking.md#phase-boundaries).

## System Ordering

Control execution order within a phase using `[ExecuteAfter]` and `[ExecuteBefore]`:

```csharp
[ExecuteAfter(typeof(SpawnSystem))]
public partial class LifetimeSystem : ISystem { ... }

[ExecuteBefore(typeof(RenderSystem))]
public partial class PhysicsSystem : ISystem { ... }
```

Order constraints can also be declared at the builder level:

```csharp
new WorldBuilder()
    .AddSystemOrderConstraint(typeof(SpawnSystem), typeof(LifetimeSystem))
    // ...
```

### ExecutePriority

Use `[ExecutePriority]` to influence ordering when no explicit constraints apply. The default priority is `0`. Lower values run earlier, higher values run later:

```csharp
[ExecutePriority(-10)]  // Runs before systems with default priority
public partial class EarlySystem : ISystem { ... }

[ExecutePriority(10)]   // Runs after systems with default priority
public partial class LateSystem : ISystem { ... }
```

`[ExecuteAfter]` and `[ExecuteBefore]` constraints always take precedence over priority — priority only breaks ties among systems with no ordering constraints between them.

## OnReady Hook

A system can declare `partial void OnReady()` to run one-time setup once the world is fully built but before the first tick. This is the right place to cache references, validate world state, or precompute data that depends on the registered entity types.

```csharp
public partial class RendererSystem : ISystem
{
    readonly List<RenderInfo> _renderables = new();

    partial void OnReady()
    {
        // World, World.WorldInfo, queries, and other systems' state are all available here.
        foreach (var info in _renderables)
        {
            foreach (var group in World.WorldInfo.GetGroupsWithTags(info.Tags))
            {
                // ...
            }
        }
    }

    public void Execute() { /* ... */ }
}
```

`OnReady` is wired by source generation — declare it as a `partial void` and leave the implementation in the system's main partial. If you don't need it, simply don't declare it.

### OnReady Ordering

`OnReady` runs in the same order systems will execute, so a system can rely on any state set up by systems that run before it:

1. **By phase** — `EarlyPresentation` → `Input` → `Fixed` → `Presentation` → `LatePresentation`
2. **Within each phase** — `[ExecuteAfter]`, `[ExecuteBefore]`, and `[ExecutePriority]` are honored, exactly as they are at execution time

Order is *not* tied to the order systems were passed to `AddSystem` / `AddSystems`. If you need one system's `OnReady` to run after another's, declare it with `[ExecuteAfter]` (or place both in the appropriate phases) — the same constraint that controls runtime order also controls `OnReady` order.

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

