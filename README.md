# Trecs

[![Source Generator](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml)
[![Unity](https://github.com/svermeulen/trecs/actions/workflows/unity.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/unity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Unity 6000.3+](https://img.shields.io/badge/Unity-6000.3%2B-black)

A high-performance Entity Component System framework for Unity, designed for deterministic simulation, recording/playback, and Burst/Jobs integration.

## Features

- **High-performance storage** — Components are stored in contiguous arrays (structure-of-arrays), grouped by explicit tags for cache-friendly iteration
- **Serialization** — Full world state serialization out of the box, including all entities, components, and heap data
- **Bookmarks, Recording & Playback** — Save and load snapshots of full game state, record and replay inputs deterministically with checksum-based desync detection, or use for network rollbacks
- **Burst & Jobs** — First-class support for Unity's job system and Burst compiler with automatic dependency tracking
- **Source generation** — Roslyn-powered code generation eliminates boilerplate for systems, aspects, and templates
- **Aspects** — Bundled component access that groups related read/write operations into a single reusable struct
- **Sets** — Dynamic entity subsets without group changes, for efficient sparse iteration and overlapping membership
- **Interpolation** — Built-in fixed-to-variable timestep interpolation for smooth rendering
- **Heap & Pointers** — `SharedPtr`, `UniquePtr`, and native variants for storing managed or large data outside of components
- **Deterministic simulation** — Fixed-timestep loop with deterministic RNG and isolated input handling, designed for networking and replay
- **Input system** — Frame-isolated input queuing that integrates with deterministic replay
- **Template system** — Composable entity blueprints with tag-based grouping and inheritance

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

world.AddSystem(new MovementSystem());
    
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

See full documentation at **[svermeulen.github.io/trecs](https://svermeulen.github.io/trecs)**.

## Samples

The project includes 12 samples covering everything from basic entity creation to complex simulations with Burst jobs. To try them, clone the repo, open `UnityProject/Trecs` in Unity 6000.3+, and run `Assets/Samples/Main.unity`.

| Sample | Concepts |
|--------|----------|
| 01 Hello Entity | Components, tags, templates, systems |
| 02 Spawn & Destroy | Entity lifecycle, dynamic spawning |
| 03 Aspects | Bundled component access for clean iteration |
| 04 Predator Prey | Cross-entity references, template inheritance |
| 05 Job System | Burst compilation, parallel jobs |
| 06 States | Template states, state transitions |
| 07 Feeding Frenzy | Complex multi-system simulation |
| 08 Sets | Dynamic entity subsets, sparse iteration, overlapping membership |
| 09 Interpolation | Fixed-to-variable timestep smoothing |
| 10 Pointers | Storing memory outside of components |
| 11 Snake | Complete game with recording/playback |
| 12 Feeding Frenzy Benchmark | Exhaustive examples of the many Trecs patterns available |

## Acknowledgments

Trecs was originally based on [Svelto.ECS](https://github.com/sebas77/Svelto.ECS) by Sebastiano Mandala

## License

[MIT](LICENSE)
