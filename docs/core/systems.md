# Systems

Systems contain the per-frame logic that runs over entities. Every system implements `ISystem`.

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

- Systems are `partial class` — the source generator fills in the rest.
- You construct systems yourself (`new SpinnerSystem(...)`) and register them with [`WorldBuilder`](world-setup.md#worldbuilder).
- `World` is a source-generated property that returns the system's [`WorldAccessor`](../advanced/accessor-roles.md). It is *not* the `World` class itself; it only exists inside types the source generator processes (systems, event handlers, `[ForEachEntity]` hosts).

## ForEachEntity

`[ForEachEntity]` marks a method for source-generated entity iteration. The generator emits the query and loop.

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
- **Aspect** — `in MyAspect` for bundled access. One aspect per method. See [Aspects](../data-access/aspects.md).

**Optional extras:**

- **`EntityAccessor`** — a live single-entity view; offers `Get<T>()`, `Remove()`, `SetTag<T>()` / `UnsetTag<T>()`, set / input ops, and a `Handle` property. Default choice when you need to act on the iterated entity.
- **`EntityHandle`** — the stable handle for the iterated entity, when you only need to store it (e.g. on another component).
- **`WorldAccessor`** — the system's accessor (main-thread only).
- **`NativeWorldAccessor`** — job-safe world access (`[WrapAsJob]` only).
- **`[GlobalIndex] int`** — see [Cross-group index](#cross-group-index).
- **`[PassThroughArgument]`** — a value the caller forwards in. See [PassThroughArgument](#passthroughargument).
- **`[SingleEntity]`** — a singleton entity hoisted out of the loop. See [SingleEntity](#singleentity).

> A low-level `EntityIndex` parameter is also accepted for advanced cases — a transient index into the underlying buffers that skips the per-call handle lookup. Only stable until the next submission. Prefer `EntityAccessor` / `EntityHandle` unless you have a specific perf reason.

### The Execute method

A system has exactly one method named `Execute`. It takes one of three forms:

- **`[ForEachEntity]`** — source-generated iteration. The most common form.
- **Plain `Execute()`** — manual entry point where you write your own queries and loops. Required when a system has multiple `[ForEachEntity]` methods, so you can call them in order.
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
                enemy.Remove(World);
        }
    }

    partial struct EnemyView : IAspect, IRead<Health> { }
}
```

### Multiple ForEachEntity methods

A system can iterate several entity groups. Provide an explicit `Execute()` that calls each in order:

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

Systems run in one of five phases, controlled by `[ExecuteIn(...)]`. Each rendered frame, they execute in this order:

| Phase | Attribute | Typical use |
|-------|-----------|-------------|
| `EarlyPresentation` | `[ExecuteIn(SystemPhase.EarlyPresentation)]` | Variable-cadence sampling that needs to run before fixed update |
| `Input` | `[ExecuteIn(SystemPhase.Input)]` | Reading player input — runs just-in-time before each fixed step |
| `Fixed` | *(default)* | Deterministic simulation, physics, game logic |
| `Presentation` | `[ExecuteIn(SystemPhase.Presentation)]` | Rendering, transform sync, interpolation reads |
| `LatePresentation` | `[ExecuteIn(SystemPhase.LatePresentation)]` | Post-animation corrections — driven by `World.LateTick` |

The fixed phase runs at a fixed timestep (default 1/60s) and may run multiple times per rendered frame to catch up — or zero times if rendering is faster than the fixed rate. Each fixed step is preceded by the input phase. Presentation and LatePresentation run once per rendered frame.

```text
Unity Update()  ──▶  World.Tick()

      ┌───────────────────────┐
      │   EarlyPresentation   │   once per Tick
      └───────────┬───────────┘
                  │
      ┌───────────▼───────────────────────┐
      │  Fixed catch-up loop              │   0..N iterations,
      │                                   │   based on accumulated
      │       ┌─────────────────┐         │   variable time
      │       │      Input      │         │
      │       └────────┬────────┘         │
      │                ▼                  │
      │       ┌─────────────────┐         │
      │       │      Fixed      │         │
      │       └────────┬────────┘         │
      │                ▼                  │
      │       ┌─────────────────┐         │
      │       │   submission    │         │
      │       └─────────────────┘         │
      └───────────┬───────────────────────┘
                  │
      ┌───────────▼───────────┐
      │     Presentation      │   once per Tick
      └───────────────────────┘


Unity LateUpdate()  ──▶  World.LateTick()

      ┌───────────────────────┐
      │    LatePresentation   │   once per LateTick
      └───────────┬───────────┘
                  │
                  ▼
          ┌───────────────┐
          │  submission   │   final flush
          └───────────────┘
```

Trecs doesn't hook into Unity's update loop. Drive it from a `MonoBehaviour`:

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

Outstanding jobs complete at every phase boundary. See [Dependency Tracking](../performance/dependency-tracking.md#phase-boundaries).

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

As an alternative to per-pair constraints, `[ExecutePriority(int)]` positions a system broadly within its phase (default `0`; higher = later). Useful when you want a system to run before *everything* (or after everything) without naming each peer:

```csharp
[ExecutePriority(-10)]  // Runs before systems with default priority
public partial class EarlySystem : ISystem { }

[ExecutePriority(10)]   // Runs after systems with default priority
public partial class LateSystem : ISystem { }
```

Explicit constraints (`[ExecuteAfter]` / `[ExecuteBefore]`) always win over priority. Priority only orders systems with no constraint between them.

## OnReady hook

Declare `partial void OnReady()` on a system to run one-time setup once the world is fully built but before the first tick. Two common uses:

- **Subscribing to entity lifecycle events** — registering at `OnReady` time means no spawns are missed from frame zero onward. See [Entity Events](../entity-management/entity-events.md).
- **Initializing global components** — the global entity exists by `OnReady`, so `World.GlobalComponent<T>().Write` is available.

```csharp
public partial class EnemyTracker : ISystem
{
    IDisposable _enemyAddedSub;

    partial void OnReady()
    {
        _enemyAddedSub = World.Events.EntitiesWithTags<GameTags.Enemy>().OnAdded(OnEnemyAdded);
    }

    partial void OnShutdown() => _enemyAddedSub?.Dispose();

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

`OnReady` runs in execution order: by phase, then by `[ExecuteAfter]` / `[ExecuteBefore]`, then `[ExecutePriority]`, then registration order as the tie-breaker. To make one system's `OnReady` run after another's, use `[ExecuteAfter]` — the same constraint controls runtime order.

## OnShutdown hook

Declare `partial void OnShutdown()` on a system to run one-time teardown when the world is disposed — releasing native resources, unsubscribing from external events, flushing final state.

```csharp
public partial class RendererSystem : ISystem
{
    GraphicsBuffer _instanceBuffer;

    partial void OnReady() => _instanceBuffer = new GraphicsBuffer(/* ... */);
    partial void OnShutdown() => _instanceBuffer?.Release();
}
```

`OnShutdown` runs in **reverse** `OnReady` order, so a system can rely on its `[ExecuteAfter]` dependencies still being alive when it tears down.

Both hooks are source-generated. Declare them as `partial void` when you need them and omit them when you don't.

### What the world looks like inside OnShutdown

Just before the first `OnShutdown` hook runs, the world removes every non-global entity (firing `OnRemoved` observers one last time). So inside `OnShutdown`:

- **Non-global queries are empty** — `Count()`, `[ForEachEntity]`, etc. see zero entities.
- **The global entity is still alive** — `World.GlobalComponent<T>()` still works.
- **`OnRemoved` observers already fired** for everything else, so cleanup keyed off entity removal has happened. Release any resources you allocated in `OnReady` here.

## Registering systems

Register systems with the world builder, or on the `World` after `Build()` when constructors need a live `World`. See [World Setup](world-setup.md#adding-systems).

```csharp
new WorldBuilder()
    .AddSystem(new SpinnerSystem(rotationSpeed: 2f))
    .AddSystem(new SpinnerGameObjectUpdater(gameObjectRegistry))
    .AddSystem(new LifetimeSystem())
    .BuildAndInitialize();
```

---

## Less common features

### PassThroughArgument

`[PassThroughArgument]` lets the caller pass values into a `[ForEachEntity]` method. The generated method takes one parameter per attributed argument:

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

Tags must be hardcoded in the attribute (e.g. `[SingleEntity(typeof(MyTag))]`) — there's no runtime form and no `Optional` mode. For a dynamic tag or no-throw form, call `World.Query().WithTags(...).Single()` (or `TrySingle`) directly.

`[SingleEntity]` works in four contexts:

- **Plain `Execute`** — runs once per call; every singleton is hoisted before the body.
- **Mixed with `[ForEachEntity]`** — the singleton is resolved once before the loop and reused for every iteration.
- **`[WrapAsJob]` static methods** — singletons become job-struct fields wired up at schedule time.
- **Hand-written job-struct fields** — `[SingleEntity]` directly on a field of an `IJobFor` makes the generator populate it (same as `[FromWorld]` for other field kinds).

For a complete `[SingleEntity]` example tracking a single head entity, see [Sample 11 — Snake](../samples/11-snake.md).

### Cross-group index

Mark an `int` parameter with `[GlobalIndex]` to receive a unique index spanning every group iterated by the call. The first entity gets `0`, the next `1`, and so on through `total − 1`, even across multiple groups.

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

Useful when filling a contiguous output buffer across groups — e.g. packed data for instanced rendering. The per-group iteration index resets per group; `[GlobalIndex]` doesn't.

### Entity operations from inside a system

Systems can mutate world state through the source-generated `World` property:

```csharp
World.AddEntity<SampleTags.Sphere>()
    .Set(new Position(float3.zero))
    .Set(new Lifetime(5f));

World.RemoveEntity(handle);
ball.UnsetTag<BallTags.Active>(World);   // partition transition

float dt = World.DeltaTime;
float random = World.Rng.Next();
```

See [Structural Changes](../entity-management/structural-changes.md) for the deferred-submission semantics.
