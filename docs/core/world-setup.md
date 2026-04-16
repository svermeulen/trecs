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
| `SetSettings(WorldSettings)` | Configure timing, determinism, and debug options |
| `AddEntityType(Template)` | Register an entity template |
| `AddEntityTypes(IEnumerable<Template>)` | Register multiple templates |
| `AddSet<T>()` | Register an entity set for filtering |
| `AddBlobStore(IBlobStore)` | Register a blob store for heap data |
| `SetBlobCacheSettings(BlobCacheSettings)` | Configure blob caching |
| `AddSystemOrderConstraint(params Type[])` | Define system execution order |
| `Build()` | Build the world (returns `World`) |
| `BuildAndInitialize()` | Build and immediately initialize |

### Adding Systems

Systems are added to the world after `Build()` and before `Initialize()`:

```csharp
world.AddSystems(new ISystem[]
{
    new PhysicsSystem(),
    new RenderSystem(gameObjectRegistry),
});
```

This is the standard pattern used throughout Trecs. Adding systems post-Build allows system constructors to receive dependencies that require a built `World` instance.

| Method | Description |
|--------|-------------|
| `World.AddSystem(ISystem)` | Register a single system |
| `World.AddSystems(IEnumerable<ISystem>)` | Register multiple systems |

## WorldSettings

```csharp
// Values below are the defaults, shown here for reference. Only set the options you need to change.
var settings = new WorldSettings
{
    // Timing
    FixedTimeStep = 1f / 60f,
    MaxSecondsForFixedUpdatePerFrame = null,    // Cap on fixed update time per frame

    // Determinism
    RandomSeed = null,                          // Seed for deterministic RNG, set to an integer to enable a fixed value (otherwise will use System.Environment.TickCount)
    RequireDeterministicSubmission = false,     // Sort structural ops for replay

    // Startup
    StartPaused = false,

    // Events
    TriggerRemoveEventsOnDispose = true,        // Fire removal events on world dispose

    // Debug warnings
    WarnOnFixedUpdateFallingBehind = false,
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
    .AddEntityType(...)
    .Build();

// 2. Add systems (between Build and Initialize)
world.AddSystem(new FooSystem());
world.AddSystems(new ISystem[] { ... });

// 3. Initialize (allocates groups, initializes systems)
world.Initialize();

// 4. Create an accessor for interacting with the world
var world = world.CreateAccessor();

// 5. Game loop
while (running)
{
    world.Tick();       // Runs input, fixed, and variable update systems
    world.LateTick();   // Runs late variable update systems
}

// 6. Cleanup
world.Dispose();
```

!!! note
    `BuildAndInitialize()` combines steps 1 and 3, skipping step 2. Use this only when no systems need to be added post-Build.

## WorldAccessor

`WorldAccessor` is the primary API for interacting with the world at runtime. Systems receive it automatically via source generation, but you can also create one manually:

```csharp
var world = world.CreateAccessor();
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
