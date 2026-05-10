# Systems

Systems contain the logic that runs over entities each frame. Every system implements `ISystem`.

## A first system

```csharp
public partial class SpinnerSystem : ISystem
{
    readonly float _rotationSpeed;

    public SpinnerSystem(float rotationSpeed) => _rotationSpeed = rotationSpeed;

    [ForEachEntity(typeof(Spinner))]
    void Execute(ref Rotation rotation)
    {
        float angle = World.DeltaTime * _rotationSpeed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

Three things to know:

- Systems are `partial class` — the source generator fills in the rest.
- You construct systems yourself (`new SpinnerSystem(...)`) and register them with [`WorldBuilder`](world-setup.md#worldbuilder).
- `World` is a source-generated property that returns the system's [`WorldAccessor`](../advanced/accessor-roles.md). It is *not* the `World` class itself; it only exists inside types the source generator processes (systems, event handlers, `[ForEachEntity]` hosts).

## ForEachEntity

`[ForEachEntity]` marks a method for source-generated entity iteration. The generator emits the query and loop for you.

### Scoping

```csharp
// Single tag
[ForEachEntity(typeof(SampleTags.Spinner))]
void Execute(ref Rotation rotation) { ... }

// Multiple tags (entities must have all of them)
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void Execute(in ActiveBall ball) { ... }

// Match by component shape — every group whose template declares these components
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position position, in Velocity velocity) { ... }

// Restrict to members of a set
[ForEachEntity(Set = typeof(SampleSets.HighlightedParticle))]
void Execute(in ParticleView particle) { ... }
```

See [Sets](../entity-management/sets.md) for set-scoped iteration.

### Parameters

`[ForEachEntity]` methods accept these parameter shapes. The generator wires each one automatically.

**Component data** (pick one style per method):

- **Component refs** — `ref T` (read-write) or `in T` (read-only) for `IEntityComponent` types. Multiple components can be listed.
- **Aspect** — `in MyAspect` for bundled access. Only one aspect per method. See [Aspects](../data-access/aspects.md).

**Optional extras:**

- **`EntityIndex`** — the current entity's transient index.
- **`WorldAccessor`** — the system's accessor (main-thread only).
- **`NativeWorldAccessor`** — job-safe world access (`[WrapAsJob]` only).
- **`[GlobalIndex] int`** — see [Cross-group index](#cross-group-index).
- **`[PassThroughArgument]`** — a value the caller forwards in. See [PassThroughArgument](#passthroughargument).
- **`[SingleEntity]`** — a singleton entity hoisted out of the loop. See [SingleEntity](#singleentity).

### The Execute method

A system has exactly one method named `Execute`. It can take three forms:

- **`[ForEachEntity]`** — source-generated iteration. The most common form.
- **Plain `Execute()`** — manual entry point where you write your own queries and loops. Required when a system has multiple `[ForEachEntity]` methods, since you need to call them in order.
- **`[WrapAsJob]` static method** — a `[ForEachEntity]` method that runs as a Burst-compiled parallel job. See [Jobs & Burst](../performance/jobs-and-burst.md).

```csharp
// Manual Execute
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
```

### Multiple ForEachEntity methods

A system can iterate several entity groups. Provide an explicit `Execute()` that calls each in the order you want:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class BallRendererSystem : ISystem
{
    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
    void RenderActive(in ActiveBallView ball) { /* ... */ }

    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Resting))]
    void RenderResting(in RestingBallView ball) { /* ... */ }

    public void Execute()
    {
        RenderActive();
        RenderResting();
    }

    partial struct ActiveBallView : IAspect, IRead<Position, GameObjectId> { }
    partial struct RestingBallView : IAspect, IRead<Position, GameObjectId> { }
}
```

## Update phases

Systems run in one of five phases, controlled by `[ExecuteIn(...)]`. Each rendered frame they execute in this order:

| Phase | Attribute | Typical use |
|-------|-----------|-------------|
| `EarlyPresentation` | `[ExecuteIn(SystemPhase.EarlyPresentation)]` | Variable-cadence sampling that needs to run before fixed update |
| `Input` | `[ExecuteIn(SystemPhase.Input)]` | Reading player input — runs just-in-time before each fixed step |
| `Fixed` | *(default)* | Deterministic simulation, physics, game logic |
| `Presentation` | `[ExecuteIn(SystemPhase.Presentation)]` | Rendering, transform sync, interpolation reads |
| `LatePresentation` | `[ExecuteIn(SystemPhase.LatePresentation)]` | Post-animation corrections — driven by `World.LateTick` |

The fixed phase runs at a fixed timestep (default 1/60s) and may run multiple times per rendered frame to catch up — or zero times if rendering is faster than the fixed rate. Each fixed step is preceded by the input phase. Presentation and LatePresentation run once per rendered frame.

Trecs doesn't hook into Unity's update loop automatically — drive it from a `MonoBehaviour`:

```csharp
public class GameLoop : MonoBehaviour
{
    World _world;

    void Start()
    {
        _world = new WorldBuilder()
            .AddTemplate(PlayerEntity.Template)
            .AddSystem(new MovementSystem())
            .BuildAndInitialize();
    }

    void Update()     => _world.Tick();      // EarlyPresentation → Input+Fixed catch-up → Presentation
    void LateUpdate() => _world.LateTick();  // LatePresentation
    void OnDestroy()  => _world.Dispose();
}
```

All outstanding jobs are completed at every phase boundary. See [Dependency Tracking](../performance/dependency-tracking.md#phase-boundaries).

## System ordering

Within a phase, declare execution order with `[ExecuteAfter]` and `[ExecuteBefore]`:

```csharp
[ExecuteAfter(typeof(SpawnSystem))]
public partial class LifetimeSystem : ISystem { }

[ExecuteBefore(typeof(RenderSystem))]
public partial class PhysicsSystem : ISystem { }
```

Or declare the constraint at the builder level:

```csharp
new WorldBuilder()
    .AddSystemOrderConstraint(typeof(SpawnSystem), typeof(LifetimeSystem))
    // ...
```

As an alternative to per-pair constraints, `[ExecutePriority(int)]` lets a system position itself broadly within its phase (default `0`; higher = later). Useful when you want a system to run before *everything* (or after everything) without naming each peer:

```csharp
[ExecutePriority(-10)]  // Runs before systems with default priority
public partial class EarlySystem : ISystem { }

[ExecutePriority(10)]   // Runs after systems with default priority
public partial class LateSystem : ISystem { }
```

Explicit constraints (`[ExecuteAfter]` / `[ExecuteBefore]`) always win over priority — priority only orders systems with no constraint between them.

## OnReady hook

Declare `partial void OnReady()` on a system to run one-time setup once the world is fully built — including the global entity — but before the first tick. The two most common uses are **subscribing to entity lifecycle events** (registering at `OnReady` time means no spawns are missed from frame zero onward — see [Entity Events](../entity-management/entity-events.md)) and **initializing global components** (the global entity exists by the time `OnReady` runs, so writes through `World.GlobalComponent<T>().Write` apply directly).

```csharp
public partial class EnemyTracker : ISystem
{
    partial void OnReady()
    {
        World.Events.EntitiesWithTags<GameTags.Enemy>().OnAdded(OnEnemyAdded);
    }

    [ForEachEntity]
    void OnEnemyAdded(in Health hp) { /* ... */ }
}

public partial class ScoreSystem : ISystem
{
    readonly int _startingLives;

    public ScoreSystem(int startingLives) => _startingLives = startingLives;

    partial void OnReady()
    {
        World.GlobalComponent<Score>().Write = new Score { Lives = _startingLives };
    }
}
```

Note that the global entity is submitted before any `OnReady` hook runs, so an `OnAdded` subscription registered in `OnReady` will not fire for the global entity itself — read its components directly via `World.GlobalComponent<T>()` instead.

`OnReady` is wired by source generation — declare it as a `partial void` and leave the implementation in the system's main partial. Don't declare it if you don't need it.

`OnReady` runs in the same order systems will execute: by phase first (`EarlyPresentation` → `Input` → `Fixed` → `Presentation` → `LatePresentation`), then by `[ExecuteAfter]` / `[ExecuteBefore]` / `[ExecutePriority]` within each phase. Order is **not** tied to the order systems were passed to `AddSystem` / `AddSystems`. If you need one system's `OnReady` to run after another's, declare it with `[ExecuteAfter]` — the same constraint controls runtime order.

## Registering systems

Systems are registered with the world builder:

```csharp
new WorldBuilder()
    .AddSystem(new SpinnerSystem(rotationSpeed: 2f))
    .AddSystem(new SpinnerGameObjectUpdater(gameObjectRegistry))
    .AddSystem(new LifetimeSystem())
    .BuildAndInitialize();
```

You can also register on the `World` after `Build()` — useful when system constructors need a live `World`. See [World Setup](world-setup.md#adding-systems).

---

## Less common features

### PassThroughArgument

`[PassThroughArgument]` lets the caller pass values into a `[ForEachEntity]` method. The generated method takes one parameter per `[PassThroughArgument]`:

```csharp
public partial class ParticleBoundSystem : ISystem
{
    readonly float _halfSize;

    [ForEachEntity(typeof(SampleTags.Particle))]
    [WrapAsJob]
    static void ExecuteAsJob(
        ref Velocity velocity,
        ref Position position,
        [PassThroughArgument] float halfSize)
    {
        if (position.Value.x > halfSize || position.Value.x < -halfSize)
            velocity.Value.x = -velocity.Value.x;
    }

    public void Execute() => ExecuteAsJob(_halfSize);
}
```

Useful for configuration values or precomputed data that aren't components on the iterated entities. Works with both main-thread and `[WrapAsJob]` methods. Values must be unmanaged when used with jobs.

### SingleEntity

`[SingleEntity]` resolves a parameter to the unique entity with a given tag. The framework runs the equivalent of `World.Query().WithTags<...>().Single()` once before the body, asserts exactly one match, and binds the result.

```csharp
void Execute([SingleEntity(typeof(GlobalTag))] ref Score score)
{
    score.Value += 1;
}
```

Tags are required and must be specified inline — `[SingleEntity]` has no runtime override or `Optional` mode. For dynamic tag lookup, call `World.Query().WithTags(...).Single()` directly.

`[SingleEntity]` works in four contexts:

- **Plain `Execute`** — runs once per call; every singleton is hoisted before the body.
- **Mixed with `[ForEachEntity]`** — the singleton is resolved once before the loop and reused for every iterated entity.
- **`[WrapAsJob]` static methods** — singletons become job-struct fields wired up at schedule time.
- **Hand-written job-struct fields** — `[SingleEntity]` directly on a field of an `IJobFor` makes the generator populate it (the same way `[FromWorld]` works for other field kinds).

For a worked example using `[SingleEntity]` to track a single head entity, see [Sample 11 — Snake](../samples/11-snake.md).

### Cross-group index

Mark an `int` parameter with `[GlobalIndex]` to receive a unique index spanning every group iterated by the call. The first entity gets `0`, the next `1`, and so on through `total − 1` — even when the iteration covers multiple groups.

```csharp
[BurstCompile]
partial struct BuildInstanceData
{
    [WriteOnly] public NativeArray<InstanceData> Instances;

    [ForEachEntity]
    [WrapAsJob]
    public void Execute(in Position position, [GlobalIndex] int globalIndex)
    {
        Instances[globalIndex] = new InstanceData { Position = position.Value };
    }
}
```

Useful when filling a contiguous output buffer across groups — for example, packed data for instanced rendering. `EntityIndex` resets per group; `[GlobalIndex]` doesn't.

### Entity operations from inside a system

Systems can mutate world state through the source-generated `World` property:

```csharp
World.AddEntity<SampleTags.Sphere>()
    .Set(new Position(float3.zero))
    .Set(new Lifetime(5f));

World.RemoveEntity(entityIndex);
World.MoveTo<BallTags.Ball, BallTags.Resting>(ball.EntityIndex);

float dt = World.DeltaTime;
float random = World.Rng.Next();
```

See [Structural Changes](../entity-management/structural-changes.md) for the deferred-submission semantics.
