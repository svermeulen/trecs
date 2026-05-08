# Trecs

A high-performance Entity Component System framework for Unity, designed for deterministic simulation, recording/playback, and Burst/Jobs integration.

## Features

- **High-performance storage** — Components are stored in contiguous arrays (structure-of-arrays), grouped by tag set for cache-friendly iteration.
- **Burst & Jobs** — First-class support for Unity's job system and Burst compiler, with automatic dependency tracking based on declared component access.
- **Source generation** — Roslyn-powered code generation eliminates boilerplate for systems, aspects, templates, and jobs.
- **Aspects** — Reusable bundles of read/write component access that systems iterate over a single entity at a time.
- **Sets** — Dynamic entity subsets that can overlap freely for sparse iteration without restructuring storage.
- **Heap & Pointers** — `SharedPtr` and `UniquePtr` for storing native or managed data outside of components.
- **Interpolation** — Built-in fixed-to-variable timestep interpolation for smooth rendering off a deterministic simulation.
- **Templates** — Composable entity blueprints that describe component layouts for common entity types.
- **Deterministic simulation** — Fixed-timestep loop with deterministic RNG and an isolated input queue, designed for replay and rollback.
- **Snapshots, Recording & Playback** — Full game state serialization, replayable input recordings with checksum desync detection, and snapshot/scrub editor tooling.

## Quick Start

```csharp
// 1. Define components
[Unwrap]
public partial struct Position : IEntityComponent { public float3 Value; }

[Unwrap]
public partial struct Velocity : IEntityComponent { public float3 Value; }

// 2. Define a tag
public struct PlayerTag : ITag { }

// 3. Define an entity template
public partial class PlayerEntity : ITemplate, IHasTags<PlayerTag>
{
    Position Position;
    Velocity Velocity;
}

// 4. Define a system
public partial class MovementSystem : ISystem
{
    [ForEachEntity(typeof(PlayerTag))]
    void Execute(in Player player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }

    partial struct Player : IAspect, IRead<Velocity>, IWrite<Position> { }
}

// 5. Build and run the world
var world = new WorldBuilder()
    .AddEntityType(PlayerEntity.Template)
    .AddSystem(new MovementSystem())
    .Build();

world.Initialize();

// In a MonoBehaviour:
void Update()    { world.Tick(); }
void OnDestroy() { world.Dispose(); }
```

A few things in that snippet that are worth knowing up front:

- `[Unwrap]` lets aspects expose the component's inner field directly, so `player.Position` is a `float3` rather than a `Position` wrapper. See [Components — the `[Unwrap]` shorthand](core/components.md#the-unwrap-shorthand).
- `World` inside a system body is a source-generated **instance property** (a [`WorldAccessor`](data-access/aspects.md)), not the `World` class. Outside systems, you create a `WorldAccessor` from `World.CreateAccessor(...)` explicitly.

For a complete walkthrough that creates entities and runs them in a Unity scene, see [Getting Started](getting-started.md).
