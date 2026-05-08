# Samples

Trecs includes a progressive tutorial series and two full game samples. Each sample builds on concepts from previous ones.

## Tutorial Series (Trecs 101)

| # | Sample | Concepts |
|---|--------|----------|
| 01 | [Hello Entity](01-hello-entity.md) | Components, tags, templates, systems, world setup |
| 02 | [Spawn & Destroy](02-spawn-and-destroy.md) | Entity lifecycle, dynamic spawning, timed destruction |
| 03 | [Aspects](03-aspects.md) | Bundled component access for clean iteration |
| 04 | [Predator Prey](04-predator-prey.md) | Cross-entity references, template inheritance, event cleanup |
| 05 | [Job System](05-job-system.md) | Burst compilation, parallel jobs, WrapAsJob |
| 06 | [Partitions](06-partitions.md) | Template partitions, partition transitions, group-based iteration |
| 07 | [Feeding Frenzy](07-feeding-frenzy.md) | Complex simulation, visual smoothing, multiple interacting systems |
| 08 | [Sets](08-sets.md) | Dynamic entity subsets, overlapping membership |
| 09 | [Interpolation](09-interpolation.md) | Fixed-to-variable timestep interpolation |
| 10 | [Pointers](10-pointers.md) | SharedPtr, UniquePtr, managed data on the heap |
| 14 | [Native Pointers](14-native-pointers.md) | NativeSharedPtr / NativeUniquePtr accessed from a Burst job |
| 15 | [Aspect Interfaces](15-aspect-interfaces.md) | Shared aspect contracts via `partial interface` for reusable cross-species helpers |
| 17 | [Blob Storage](17-blob-storage.md) | Content-pipeline-stable `BlobId`s for shared immutable assets on the heap |
| 18 | [Reactive Events](18-reactive-events.md) | `OnAdded` / `OnRemoved` observers for cleanup and stat tracking |
| 19 | [Multiple Worlds](19-multiple-worlds.md) | Two independent `World` instances ticking side-by-side |

## Game Samples

| # | Sample | Description |
|---|--------|-------------|
| 11 | [Snake](11-snake.md) | Complete grid-based game with input handling and recording/playback |
| 12 | [Feeding Frenzy Benchmark](12-feeding-frenzy-benchmark.md) | Performance benchmark comparing partition approaches and iteration styles |
| 13 | [Save Game](13-save-game.md) | Sokoban puzzle demonstrating snapshot-based save/load slots |

## Running the Samples

Open `UnityProject/Trecs/Assets/Samples/Main.unity` in Unity 6000.3+ and press Play. The scene includes a `SampleCycler` that lets you switch between samples at runtime. Each sample has its own composition root that builds the world and initializes the scene.

## Sample Architecture: Bootstrap & CompositionRoot

Each sample uses a simple two-class pattern to wire everything together:

- **`Bootstrap`** — a MonoBehaviour that drives the Unity lifecycle. In `Awake()` it calls `CompositionRoot.Construct()` and runs the returned initializers; `Update()` and `LateUpdate()` invoke the tickables and late tickables; `OnDestroy()` runs the disposables.
- **`CompositionRootBase`** — an abstract MonoBehaviour that each sample subclasses. `Construct()` builds the `WorldBuilder`, creates systems, and returns four `List<Action>` callbacks (init, tick, late tick, dispose).

```csharp
// Simplified — each sample's composition root looks roughly like this:
public class MyCompositionRoot : CompositionRootBase
{
    public override void Construct(
        out List<Action> initializables,
        out List<Action> tickables,
        out List<Action> lateTickables,
        out List<Action> disposables)
    {
        var world = new WorldBuilder()
            .AddTemplate(MyEntity.Template)
            .Build();

        world.AddSystems(new ISystem[] { new MySystem() });

        initializables = new() { world.Initialize };
        tickables = new() { world.Tick };
        lateTickables = new() { world.LateTick };
        disposables = new() { world.Dispose };
    }
}
```

!!! note
    This pattern is just a lightweight convenience for the samples. Trecs is deliberately unopinionated about how you structure your application — it doesn't register MonoBehaviours, manage singletons, or hook into Unity's update loop automatically. Use whatever you prefer: a DI framework (Reflex, Zenject, VContainer), plain MonoBehaviours, ScriptableObjects, or anything else. All Trecs needs is for your code to call `world.Tick()`, `world.LateTick()`, and `world.Dispose()` at the appropriate times.

## Shared Utilities

The `Common/` directory contains helpers shared across samples (these are sample code — not part of Trecs itself):

- **`GameObjectRegistry`** — maps `GameObjectId` components to Unity GameObjects
- **`RendererSystem`** — GPU-instanced indirect rendering for high entity counts
- **`Bootstrap`** / **`CompositionRootBase`** — the lifecycle wiring described above
- **`SampleUtil`** — primitive/material helpers
- **`DisposeCollection`** — small `IDisposable` aggregator
- Common components and templates (`Position`, `Rotation`, `UniformScale`, `ColorComponent`, `CommonTemplates.Renderable`, etc.)

If you copy code from a sample, be sure to also copy any dependencies from `Common/`.
