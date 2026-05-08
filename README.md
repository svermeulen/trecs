# Trecs

[![Source Generator](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml)
[![Unity](https://github.com/svermeulen/trecs/actions/workflows/unity.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/unity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Unity 6000.3+](https://img.shields.io/badge/Unity-6000.3%2B-black)

A high-performance Entity Component System framework for Unity, designed for deterministic simulation, recording/playback, and Burst/Jobs integration.

## Features

- **High-performance storage** — Components are stored in contiguous arrays (structure-of-arrays), grouped by tag set for cache-friendly iteration.
- **Burst & Jobs** — First-class support for Unity's job system and Burst compiler, with automatic dependency tracking based on declared component access.
- **Source generation** — Roslyn-powered code generation eliminates boilerplate for systems, aspects, templates, and jobs.
- **Aspects** — Reusable bundles of read/write component access that systems iterate over a single entity at a time.
- **Sets** — Dynamic entity subsets that can overlap freely for sparse iteration without restructuring storage.
- **Heap & Pointers** — `SharedPtr` and `UniquePtr` for storing native or managed data outside of components.
- **Interpolation** — Built-in fixed-to-variable timestep interpolation for smooth rendering off a deterministic simulation.
- **Templates** — Composable blueprints describing an entity's components, tags, partitions, and inheritance.
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
public partial class PlayerEntity : ITemplate, ITagged<PlayerTag>
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
    .AddTemplate(PlayerEntity.Template)
    .AddSystem(new MovementSystem())
    .Build();

world.Initialize();

// In a MonoBehaviour:
void Update()    { world.Tick(); }
void OnDestroy() { world.Dispose(); }
```

A few things in that snippet that are worth knowing up front:

- `[Unwrap]` lets aspects expose the component's inner field directly, so `player.Position` is a `float3` rather than a `Position` wrapper. See [Components — the `[Unwrap]` shorthand](https://svermeulen.github.io/trecs/core/components/#the-unwrap-shorthand).
- `World` inside a system body is a source-generated **instance property** (a [`WorldAccessor`](https://svermeulen.github.io/trecs/data-access/aspects/)), not the `World` class. Outside systems, you create a `WorldAccessor` from `World.CreateAccessor(...)` explicitly.

For a complete walkthrough that creates entities and runs them in a Unity scene, see [Getting Started](https://svermeulen.github.io/trecs/getting-started/).

## Installation

Requires Unity 6000.3+.

### Via OpenUPM (recommended)

With the [openupm-cli](https://openupm.com/):

```bash
openupm add com.trecs.core
# Optional: serialization features (snapshots, recording/playback, save/load)
openupm add com.trecs.serialization
```

Or add manually to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["com.trecs"]
    }
  ],
  "dependencies": {
    "com.trecs.core": "0.2.0",
    "com.trecs.serialization": "0.2.0"
  }
}
```

### Via Git URL

Open **Window > Package Manager**, click **+ > Add package from git URL**, and enter:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core
```

For the optional serialization package:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.serialization
```

When using git URLs, add `com.trecs.core` before `com.trecs.serialization` — Unity can't resolve versioned dependencies from git URLs.

## Documentation

See full documentation at **[svermeulen.github.io/trecs](https://svermeulen.github.io/trecs)**.

## Samples

The project includes 18 samples covering everything from basic entity creation to complex simulations with Burst jobs. To try them, clone the repo, open `UnityProject/Trecs` in Unity 6000.3+, and run `Assets/Samples/Main.unity`.

| Sample | Concepts |
|--------|----------|
| 01 Hello Entity | Components, tags, templates, systems |
| 02 Spawn & Destroy | Entity lifecycle, dynamic spawning |
| 03 Aspects | Bundled component access for clean iteration |
| 04 Predator Prey | Cross-entity references, template inheritance |
| 05 Job System | Burst compilation, parallel jobs |
| 06 Partitions | Template partitions, partition transitions |
| 07 Feeding Frenzy | Complex multi-system simulation |
| 08 Sets | Dynamic entity subsets, sparse iteration, overlapping membership |
| 09 Interpolation | Fixed-to-variable timestep smoothing |
| 10 Pointers | Storing memory outside of components |
| 11 Snake | Complete game with recording/playback |
| 12 Feeding Frenzy Benchmark | Exhaustive examples of the many Trecs patterns available |
| 13 Save Game | Snapshot-based save/load slots with the serialization package |
| 14 Native Pointers | `NativeSharedPtr` and `NativeUniquePtr` read and mutated inside a Burst job |
| 15 Aspect Interfaces | Reusing aspect logic across templates via interface composition |
| 17 Blob Storage | `BlobStore` for sharing immutable data across many entities |
| 18 Reactive Events | Subscribing to entity add / remove / move events |
| 19 Multiple Worlds | Running multiple `World` instances side by side in one scene |

## Acknowledgments

Trecs was originally based on [Svelto.ECS](https://github.com/sebas77/Svelto.ECS) by Sebastiano Mandalà.

## License

[MIT](LICENSE)
