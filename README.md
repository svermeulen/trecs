# Trecs

[![Source Generator](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml)
[![Unity](https://github.com/svermeulen/trecs/actions/workflows/unity.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/unity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Unity 6000.3+](https://img.shields.io/badge/Unity-6000.3%2B-black)

A high-performance Entity Component System framework for Unity, designed for deterministic simulation, recording/playback, and Burst/Jobs integration.

## Features

- **Group-based storage** — cache-friendly data layout for fast iteration
- **Source generation** — minimal boilerplate with Roslyn-powered code generation
- **Burst & Jobs** — first-class support for Unity's job system and Burst compiler
- **Deterministic simulation** — designed for networking and replay
- **Recording & Playback** — full simulation recording with desync detection
- **Template system** — composable entity archetypes with tags and states
- **Interpolation** — built-in fixed-to-variable timestep interpolation

## Quick Start

```csharp
// Step 1: Define components
[Unwrap] // <- Indicates that it is a single value component, so that Aspects can unwrap values directly
public partial struct Position : IEntityComponent
{
    public float3 Value;
}

// Step 2: Define entity tags
public struct PlayerTag : ITag { }

// Step 3: Define entity types
// Note that this class is never actually instantiated
// It is only used to declare the components and tags that entities of this type will have
public partial class PlayerEntity : ITemplate, IHasTags<PlayerTag>
{
    public Position Position;
    public Velocity Velocity;
}

// Step 4: Define systems to operate on entities
public partial class MovementSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Position pos, in Velocity vel)
    {
        pos.Value += vel.Value * World.DeltaTime;
    }
}

// Or alternatively, match by tag instead of components:
public partial class MovementSystem : ISystem
{
    [ForEachEntity(Tag = typeof(PlayerTag))]
    void Execute(ref Position pos, in Velocity vel)
    {
        pos.Value += vel.Value * World.DeltaTime;
    }
}

// Can also define an IAspect to group together components
// and unwrap the single value components for direct access:
public partial class MovementSystem : ISystem
{
    [ForEachEntity(Tag = typeof(PlayerTag))]
    void Execute(in Player player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }

    partial struct Player : IAspect, IRead<ApproachingFish>, IWrite<Position> { }
}

// Step 5: Define, initialize, and run the world
var world = new WorldBuilder()
    .AddEntityType(PlayerEntity.Template)
    .AddSystem(new MovementSystem())
    .Build();
    
world.Initialize();

// Call this from a MonoBehaviour Update
world.Tick();

// Call this on MonoBehaviour OnDestroy or when complete
world.Dispose();
```

## Installation

Add to your Unity project via the Package Manager:

1. Open **Window > Package Manager**
2. Click **+ > Add package from git URL**
3. Enter: `https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core`

Requires Unity 6000.3+.

## Documentation

Full documentation is available at **[svermeulen.github.io/trecs](https://svermeulen.github.io/trecs)**.

## Samples

The project includes 12 samples covering everything from basic entity creation to complex simulations with Burst jobs:

| Sample | Concepts |
|--------|----------|
| 01 Hello Entity | Components, tags, templates, systems |
| 02 Spawn & Destroy | Entity lifecycle, dynamic spawning |
| 03 Aspects | Type-safe component access bundles |
| 04 Predator Prey | Cross-entity references, template inheritance |
| 05 Job System | Burst compilation, parallel jobs |
| 06 States | Template states, state transitions |
| 07 Feeding Frenzy | Complex multi-system simulation |
| 08 Sets | Dynamic entity subsets, overlapping membership |
| 09 Interpolation | Fixed-to-variable timestep smoothing |
| 10 Pointers | SharedPtr, UniquePtr, managed heap data |
| 11 Snake | Complete game with recording/playback |
| 12 Benchmark | Performance comparison of ECS patterns |

## License

[MIT](LICENSE)
