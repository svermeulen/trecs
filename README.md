<img src="docs/assets/logo.png" alt="Trecs" width="300" align="right" />

# Trecs

[![Source Generator](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml)
[![Unity](https://github.com/svermeulen/trecs/actions/workflows/unity.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/unity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Unity 6000.3+](https://img.shields.io/badge/Unity-6000.3%2B-black)

A high-performance Entity Component System framework for Unity, designed for **deterministic simulation, recording/playback, and Burst/Jobs**.

**Full docs: [svermeulen.github.io/trecs](https://svermeulen.github.io/trecs)**

<br clear="all" />

## Why Trecs

- **Designed for determinism.** Fixed-timestep simulation, deterministic RNG, isolated input, and built-in snapshot / record / replay with desync detection.
- **Burst & Jobs out of the box.** A source generator emits the job structs and chains the right `JobHandle` dependencies based on the components you read and write — no manual wiring.
- **Cache-friendly storage.** Components live in contiguous structure-of-arrays buffers grouped by tag set.
- **Small surface, lots of leverage.** Aspects bundle component access; sets give you sparse subsets without restructuring storage; templates declare entity blueprints with inheritance and partitions; pointers (`SharedPtr` / `UniquePtr`) let components reference data on the heap.
- **Editor tooling.** A live entity inspector and a record / scrub / fork timeline window for diagnosing transient bugs.

## A taste

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

# Optional: snapshots, recording / playback, save / load
openupm add com.trecs.serialization
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
    "com.trecs.core": "0.2.0",
    "com.trecs.serialization": "0.2.0"
  }
}
```

### Via Git URL

In **Window → Package Manager**, click **+ → Add package from git URL** and enter:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core
```

For the optional serialization package:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.serialization
```

Add `com.trecs.core` first — Unity can't resolve versioned dependencies from git URLs, so order matters.

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
