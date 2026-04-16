# Samples

Trecs includes a progressive tutorial series and two full game samples. Each sample builds on concepts from previous ones.

## Tutorial Series (Trecs 101)

| # | Sample | Concepts |
|---|--------|----------|
| 01 | [Hello Entity](01-hello-entity.md) | Components, tags, templates, systems, world setup |
| 02 | [Spawn & Destroy](02-spawn-and-destroy.md) | Entity lifecycle, dynamic spawning, timed destruction |
| 03 | [Aspects](03-aspects.md) | Aspects for type-safe component access |
| 04 | [Predator Prey](04-predator-prey.md) | Cross-entity references, template inheritance, event cleanup |
| 05 | [Job System](05-job-system.md) | Burst compilation, parallel jobs, WrapAsJob |
| 06 | [States](06-states.md) | Template states, state transitions, group-based iteration |
| 07 | [Feeding Frenzy](07-feeding-frenzy.md) | Complex simulation, visual smoothing, multiple interacting systems |
| 08 | [Sets](08-sets.md) | Dynamic entity subsets, overlapping membership |
| 09 | [Interpolation](09-interpolation.md) | Fixed-to-variable timestep interpolation |
| 10 | [Pointers](10-pointers.md) | SharedPtr, UniquePtr, managed data on the heap |

## Game Samples

| # | Sample | Description |
|---|--------|-------------|
| 11 | [Snake](11-snake.md) | Complete grid-based game with input handling and recording/playback |
| 12 | [Feeding Frenzy Benchmark](12-feeding-frenzy-benchmark.md) | Performance benchmark comparing state management approaches and iteration styles |

## Running the Samples

Open `UnityProject/Trecs/Assets/Samples/Main.unity` in Unity 6000.3+ to run all samples. Each sample has its own composition root that builds the world and initializes the scene.

## Shared Utilities

The `Common/` directory contains utilities shared across samples:

- **GameObjectRegistry** — maps `GameObjectId` components to Unity GameObjects
- **RendererSystem** — GPU-instanced indirect rendering for high entity counts
- **RecordAndPlaybackController** — recording/replay with bookmark support
- **CompositionRootBase** — abstract base class for sample setup
- **CommonTemplates.Renderable** — base template with Position, Rotation, Scale, Color
