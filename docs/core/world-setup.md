# World Setup

Every Trecs application starts by building a `World` — the container that owns all entities and components, and which triggers the system update.

!!! tip "New to Trecs?"
    Start with [Getting Started](../getting-started.md) for a five-minute end-to-end walkthrough, then come back here for the deeper reference.

## WorldBuilder

Use the fluent `WorldBuilder` API to configure and construct a world:

```csharp
var world = new WorldBuilder()
    .SetSettings(new WorldSettings
    {
        FixedTimeStep = 1f / 60f,
        RandomSeed = 42,
    })
    .AddTemplate(SampleTemplates.SpinnerEntity.Template)
    .Build();

world.AddSystems(new ISystem[]
{
    new SpinnerSystem(rotationSpeed: 2f),
    new SpinnerGameObjectUpdater(gameObjectRegistry),
});

world.Initialize();
```

### Builder Methods

| Method | Description |
|--------|-------------|
| `SetDebugName(string)` | Set a human-readable name (surfaces in editor tooling) |
| `SetSettings(WorldSettings)` | Configure timing, determinism, and debug options — see [WorldSettings](#worldsettings) |
| `AddTemplate(Template)` | Register an entity [template](templates.md) |
| `AddTemplates(IEnumerable<Template>)` | Register multiple [templates](templates.md) |
| `AddSet<T>()` | Register an [entity set](../entity-management/sets.md) |
| `AddBlobStore(IBlobStore)` | Register a blob store for [heap](../advanced/heap.md) data |
| `SetBlobCacheSettings(BlobCacheSettings)` | Configure blob caching |
| `AddSystem(ISystem)` / `AddSystems(IEnumerable<ISystem>)` | Register [systems](systems.md) |
| `AddSystemOrderConstraint(params Type[])` | Declare [system ordering](systems.md#system-ordering) |
| `Build()` | Build the world (returns `World`) |
| `BuildAndInitialize()` | Build and immediately initialize |

### Adding Systems

Systems can be registered on the builder or on the world directly. The two are equivalent — pick whichever fits your composition:

```csharp
// On the builder
new WorldBuilder()
    .AddSystem(new PhysicsSystem())
    .AddSystem(new RenderSystem(gameObjectRegistry))
    .BuildAndInitialize();

// On the world (between Build and Initialize)
var world = new WorldBuilder().Build();
world.AddSystems(new ISystem[]
{
    new PhysicsSystem(),
    new RenderSystem(gameObjectRegistry),
});
world.Initialize();
```

The post-`Build()` form is useful when system constructors depend on the built `World` instance (for example, when injecting a service class which itself needs to capture a `WorldAccessor`). Both `World.AddSystem` and `World.AddSystems` must be called before `Initialize()`.

## WorldSettings

```csharp
// Values below are the defaults, shown for reference. Only set what you want to change.
var settings = new WorldSettings
{
    // Timing
    FixedTimeStep = 1f / 60f,
    MaxSecondsForFixedUpdatePerFrame = 1f / 3f, // Cap on fixed-update time per frame; null = uncapped

    // Determinism
    RandomSeed = null,                          // ulong? — set for deterministic RNG; null seeds from System.Environment.TickCount
    RequireDeterministicSubmission = false,     // Sort structural ops for replay

    // Startup
    StartPaused = false,

    // Events
    TriggerRemoveEventsOnDispose = true,        // Fire removal events on world dispose

    // Debug warnings
    WarnOnFixedUpdateFallingBehind = true,
    WarnOnJobSyncPoints = false,
    WarnOnUnusedTemplates = false,

    // Safety
    MaxSubmissionIterations = 10,               // Prevent circular submission feedback
};
```

## World Lifecycle

```csharp
// 1. Build
var world = new WorldBuilder()
    .AddTemplate(...)
    .Build();

// 2. Add systems (optional — can also be done on the builder)
world.AddSystem(new FooSystem());

// 3. Initialize (allocates groups, locks system list, runs OnReady hooks)
world.Initialize();

// 4. (Optional) Create a standalone WorldAccessor for non-system code
var worldAccessor = world.CreateAccessor(AccessorRole.Unrestricted);

// 5. Game loop
while (running)
{
    world.Tick();       // EarlyPresentation → (Input + Fixed)* → Presentation
    world.LateTick();   // LatePresentation
}

// 6. Cleanup
world.Dispose();
```

!!! note
    `BuildAndInitialize()` combines steps 1 and 3. Use it when no systems need to be added post-`Build()`.

## WorldAccessor

`WorldAccessor` is the primary API for interacting with the world at runtime. Systems receive one automatically via source generation; for non-system code (lifecycle hooks, debug tooling, event callbacks) create one manually:

```csharp
var worldAccessor = world.CreateAccessor(AccessorRole.Unrestricted);
```

The role argument controls which operations the accessor is allowed to perform — see [Accessor Roles](../advanced/accessor-roles.md) for the full matrix.

`WorldAccessor` provides:

- **[Entity operations](../entity-management/structural-changes.md)** — `AddEntity`, `RemoveEntity`, `MoveTo`
- **[Component access](components.md)** — `Component<T>`, `GlobalComponent<T>`, `ComponentBuffer<T>`
- **[Queries](../data-access/queries-and-iteration.md)** — `Query()`, `CountEntitiesWithTags<T>()`
- **[Sets](../entity-management/sets.md)** — `SetAdd<T>`, `SetRemove<T>`, `SetClear<T>`
- **[Events](../entity-management/entity-events.md)** — `Events` builder for entity lifecycle subscriptions
- **[Time](../advanced/time-and-rng.md)** — `DeltaTime`, `ElapsedTime`, `Frame` (phase-aware)
- **[RNG](../advanced/time-and-rng.md)** — `Rng`, `FixedRng`, `VariableRng`
- **[Heap](../advanced/heap.md)** — `Heap` accessor for pointer allocation
- **[Jobs](../performance/jobs-and-burst.md)** — `ToNative()` for job-safe access
