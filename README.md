
# Trecs

<img src="docs/assets/logo.png" alt="Trecs" width="300" align="right" />

[![Source Generator](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml)
[![Unity](https://github.com/svermeulen/trecs/actions/workflows/unity.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/unity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Unity 6000.3+](https://img.shields.io/badge/Unity-6000.3%2B-black)

A high-performance Entity Component System framework for Unity, designed for **deterministic simulation, recording/playback, and Burst/Jobs**.

**Full docs: [svermeulen.github.io/trecs](https://svermeulen.github.io/trecs)** 

[Getting Started](https://svermeulen.github.io/trecs/getting-started/) · [Glossary](https://svermeulen.github.io/trecs/glossary/) · [Samples](https://svermeulen.github.io/trecs/samples/) · [FAQ](https://svermeulen.github.io/trecs/guides/faq/) · [Trecs vs Unity ECS](https://svermeulen.github.io/trecs/guides/trecs-vs-unity-ecs/)
<br />
<br />
<br />

> **Status:** Trecs is a `0.x` release. While it is functional and reasonably well-tested, the API is still evolving and future updates are likely to include many breaking changes.

## Features

- **Cache-friendly storage.** Components live in contiguous structure-of-arrays buffers grouped by tag set.
- **Composable building blocks.** Aspects bundle component access; sets give sparse subsets without restructuring storage; templates declare entity blueprints with inheritance and partitions; `SharedPtr` / `UniquePtr` let components reference heap data.
- **Burst and Jobs out of the box.** A source generator emits job structs and chains `JobHandle` dependencies from the components you read and write — no manual wiring.
- **Deterministic by construction.** Fixed-timestep simulation, seeded RNG, isolated input, and built-in snapshot / record / replay with desync detection.
- **Editor tooling.** A live entity inspector and a record / scrub / fork timeline window for diagnosing transient bugs.

## Quick Start

```csharp
// 1. Components — unmanaged structs holding per-entity data
public partial struct Position : IEntityComponent { public float3 Value; }
public partial struct Velocity : IEntityComponent { public float3 Value; }

// 2. A tag — a zero-cost marker
public struct PlayerTag : ITag { }

// 3. A template — the entity blueprint
public partial class PlayerEntity : ITemplate, ITagged<PlayerTag>
{
    Position Position;
    Velocity Velocity;
}

// 4. A system — logic that runs over matching entities
public partial class MovementSystem : ISystem
{
    [ForEachEntity(typeof(PlayerTag))]
    void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * World.DeltaTime;
    }
}

// 5. Build and run
var world = new WorldBuilder()
    .AddTemplate(PlayerEntity.Template)
    .AddSystem(new MovementSystem())
    .BuildAndInitialize();

// In a MonoBehaviour:
void Update()    => world.Tick();
void OnDestroy() => world.Dispose();
```

`World` inside a system body is a source-generated property — your access into the running world for that phase. See [Getting Started](https://svermeulen.github.io/trecs/getting-started/) for the full walkthrough, or the [Glossary](https://svermeulen.github.io/trecs/glossary/) for a one-stop reference of the terminology.

## Installation

Requires Unity 6000.3+.

### Via OpenUPM (recommended)

With the [openupm-cli](https://openupm.com/):

```bash
openupm add com.trecs.core
```

Or add the scoped registry to `Packages/manifest.json` manually:

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
    "com.trecs.core": "0.2.0"
  }
}
```

### Via Git URL

In **Window → Package Manager**, click **+ → Add package from git URL** and enter:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core
```

## Samples

The project includes 17 samples covering everything from basic entity creation to complex simulations with Burst jobs. To try them, clone the repo, open `UnityProject/Trecs` in Unity 6000.3+, and run `Assets/Samples/Core/Main.unity`. See the [samples docs](https://svermeulen.github.io/trecs/samples/) for details.

| Sample | Concepts |
|--------|----------|
| 01 Hello Entity | Components, tags, templates, systems, world setup |
| 02 Spawn & Destroy | Entity lifecycle, dynamic spawning, timed destruction |
| 03 Aspects | Bundled component access for clean iteration |
| 04 Predator Prey | Cross-entity references, template inheritance, event cleanup |
| 05 Job System | Burst compilation, parallel jobs, WrapAsJob |
| 06 Partitions | Template partitions, partition transitions, partition-filtered iteration |
| 07 Feeding Frenzy | Complex simulation, visual smoothing, multiple interacting systems |
| 08 Sets | Dynamic entity subsets, overlapping membership |
| 09 Interpolation | Fixed-to-variable timestep interpolation |
| 10 Dynamic Collections | Five trail-storage strategies: UniquePtr Queue, FixedArray, FixedList, TrecsList, TrecsArray |
| 11 Snake | Complete grid-based game with input handling and recording/playback |
| 12 Feeding Frenzy Benchmark | Performance benchmark comparing partition approaches and iteration styles |
| 13 Aspect Interfaces | Shared aspect contracts via `partial interface` for reusable cross-species helpers |
| 14 Blob Seed Pattern | Content-pipeline-stable `BlobId`s for shared immutable assets on the heap |
| 15 Reactive Events | `OnAdded` / `OnRemoved` observers for cleanup and stat tracking |
| 16 Multiple Worlds | Two independent `World` instances ticking side by side |
| 17 Heightmap Blobs | Content-derived `BlobId`s via `UniqueHashGenerator`, plus a `NativeSharedPtr` Burst-job variant |

## Acknowledgments

Trecs was originally based on [Svelto.ECS](https://github.com/sebas77/Svelto.ECS) by Sebastiano Mandalà.

## License

[MIT](LICENSE)
