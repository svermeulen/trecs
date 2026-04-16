# World Setup

Every Trecs application starts by building a `World` — the container that manages all entities, components, and systems.

## WorldBuilder

Use the fluent `WorldBuilder` API to configure and construct a world:

```csharp
var world = new WorldBuilder()
    .SetSettings(new WorldSettings
    {
        FixedTimeStep = 1f / 60f,
        RandomSeed = 42,
    })
    .AddEntityType(SampleTemplates.SpinnerEntity.Template)
    .AddSystem(new SpinnerSystem(rotationSpeed: 2f))
    .AddSystem(new SpinnerGameObjectUpdater(gameObjectRegistry))
    .BuildAndInitialize();
```

### Builder Methods

| Method | Description |
|--------|-------------|
| `SetSettings(WorldSettings)` | Configure timing, determinism, and debug options |
| `AddEntityType(Template)` | Register an entity template |
| `AddEntityTypes(IEnumerable<Template>)` | Register multiple templates |
| `AddSystem(ISystem)` | Register a system |
| `AddSystems(IEnumerable<ISystem>)` | Register multiple systems |
| `AddSet<T>()` | Register an entity set for filtering |
| `AddBlobStore(IBlobStore)` | Register a blob store for heap data |
| `SetBlobCacheSettings(BlobCacheSettings)` | Configure blob caching |
| `AddSystemOrderConstraint(params Type[])` | Define system execution order |
| `Build()` | Build the world without initializing |
| `BuildAndInitialize()` | Build and immediately initialize |

## WorldSettings

```csharp
var settings = new WorldSettings
{
    // Timing
    FixedTimeStep = 1f / 60f,                  // Default: 1/60
    MaxSecondsForFixedUpdatePerFrame = 0.1f,    // Cap on fixed update time per frame

    // Determinism
    RandomSeed = 42,                            // Seed for deterministic RNG
    RequireDeterministicSubmission = true,       // Sort structural ops for replay

    // Startup
    StartPaused = false,

    // Events
    TriggerRemoveEventsOnDispose = true,        // Fire removal events on world dispose

    // Debug warnings
    WarnOnFixedUpdateFallingBehind = false,
    WarnOnJobSyncPoints = false,
    WarnOnUnusedTemplates = false,
    WarnOnMissingAssertComplete = false,

    // Safety
    MaxSubmissionIterations = 10,               // Prevent circular submission feedback
};
```

## World Lifecycle

```csharp
// 1. Build
var world = new WorldBuilder()
    .AddEntityType(...)
    .AddSystem(...)
    .Build();

// 2. Initialize (allocates groups, initializes systems)
world.Initialize();

// 3. Create an accessor for interacting with the world
var ecs = world.CreateAccessor<MyGame>();

// 4. Game loop
while (running)
{
    world.Tick();       // Runs input + fixed update systems, submits entities
    world.LateTick();   // Runs variable + late variable update systems
}

// 5. Cleanup
world.Dispose();
```

!!! note
    `BuildAndInitialize()` combines steps 1 and 2. Use `Build()` separately when you need to do additional setup between building and initializing.

## WorldAccessor

`WorldAccessor` is the primary API for interacting with the world at runtime. Systems receive it automatically, but you can also create one manually:

```csharp
// Create by type (for debug naming)
var ecs = world.CreateAccessor<MyGame>();

// Create with explicit name
var ecs = world.CreateAccessor("MyGame");
```

`WorldAccessor` provides access to:

- **Entity operations** — `AddEntity`, `RemoveEntity`, `MoveTo`
- **Component access** — `Component<T>`, `GlobalComponent<T>`, `ComponentBuffer<T>`
- **Queries** — `Query()`, `CountEntitiesWithTags<T>()`
- **Sets** — `SetAdd<T>`, `SetRemove<T>`
- **Events** — `Events` builder for entity lifecycle subscriptions
- **Time** — `DeltaTime`, `ElapsedTime`, `Frame` (phase-aware)
- **RNG** — `Rng`, `FixedRng`, `VariableRng`
- **Heap** — `Heap` accessor for pointer allocation
- **Jobs** — `ToNative()` for job-safe access

See individual documentation pages for details on each area.
