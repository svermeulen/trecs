# Trecs

A high-performance Entity Component System framework for Unity, designed for deterministic simulation, recording/playback, and Burst/Jobs integration.

## Features

- **High-performance storage** — Components are stored in contiguous arrays (structure-of-arrays), grouped by explicit tags for cache-friendly iteration
- **Serialization** — Full world state serialization out of the box, including all entities, components, and heap data
- **Bookmarks, Recording & Playback** — Save and load snapshots of full game state, record and replay inputs deterministically with checksum-based desync detection, or use for network rollbacks
- **Burst & Jobs** — First-class support for Unity's job system and Burst compiler with automatic dependency tracking based on component access
- **Source generation** — Roslyn-powered code generation eliminates boilerplate for systems, aspects, and templates
- **Aspects** — Bundled component access that groups related read/write operations into a single reusable struct
- **Sets** — Dynamic entity subsets without group changes, for efficient sparse iteration and overlapping membership
- **Interpolation** — Built-in fixed-to-variable timestep interpolation for smooth rendering
- **Heap & Pointers** — `SharedPtr`, `UniquePtr`, and native variants for storing managed or large data outside of components
- **Deterministic simulation** — Fixed-timestep loop with deterministic RNG and isolated input handling, designed for networking and replay
- **Template system** — Composable entity type blueprints with tag-based grouping and inheritance

## Quick Start

```csharp
// Step 1: Define components
[Unwrap]
public partial struct Position : IEntityComponent
{
    public float3 Value;
}

// Step 2: Define entity tags
public struct PlayerTag : ITag { }

// Step 3: Define entity types
public partial class PlayerEntity : ITemplate, IHasTags<PlayerTag>
{
    public Position Position;
    public Velocity Velocity;
}

// Step 4: Define systems to operate on entities
public partial class MovementSystem : ISystem
{
    [ForEachEntity(Tag = typeof(PlayerTag))]
    void Execute(in Player player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }

    partial struct Player : IAspect, IRead<Velocity>, IWrite<Position> { }
}

// Step 5: Define, initialize, and run the world
var world = new WorldBuilder()
    .AddEntityType(PlayerEntity.Template)
    .Build();

world.AddSystems(new ISystem[] { new MovementSystem() });
world.Initialize();

// Call this from a MonoBehaviour Update
world.Tick();

// Call this on MonoBehaviour OnDestroy or when complete
world.Dispose();
```

See [Getting Started](getting-started.md) for a complete walkthrough.
