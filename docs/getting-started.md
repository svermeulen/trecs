# Getting Started

This walkthrough builds a minimal Trecs setup: one entity, one component, one system that updates it every frame. About five minutes from a blank Unity project to a running tick.

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

When using git URLs, add `com.trecs.core` first — Unity can't resolve versioned dependencies from git URLs, so the order matters.

## Your First Entity

We'll spin a value forever. The pieces are: a component (the data), a tag (the entity kind), a template (the layout), a system (the logic), and a world (the runtime).

### 1. Define a component

Components are unmanaged structs that hold per-entity data:

```csharp
using Unity.Mathematics;

public partial struct Rotation : IEntityComponent
{
    public quaternion Value;
}
```

`partial` is required — the source generator emits a companion file with `Equals`, `GetHashCode`, and `==`/`!=` operators.

### 2. Define a tag

Tags classify entities. They're empty marker structs used both at entity-creation time and as system query filters:

```csharp
public struct Spinner : ITag { }
```

### 3. Define a template

A template is the blueprint for an entity kind. It declares which tags the entity has (via `IHasTags<...>`) and which components it carries (as fields):

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<Spinner>
{
    Rotation Rotation;
}
```

Note that this template class is never actually instantiated and instead is just used as configuration for the trecs source generators.

### 4. Write a system

A system contains the logic that runs over entities each tick. The `[ForEachEntity]` attribute tells the source generator to iterate every entity tagged with `Spinner` and call `Execute` for each:

```csharp
using Unity.Mathematics;

public partial class SpinnerSystem : ISystem
{
    readonly float _speed;

    public SpinnerSystem(float speed) { _speed = speed; }

    [ForEachEntity(typeof(Spinner))]
    void Execute(ref Rotation rotation)
    {
        var angle = World.DeltaTime * _speed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

`World` here is a source-generated property on the system — a [`WorldAccessor`](core/world-setup.md) scoped to whichever phase the system runs in.

### 5. Build and run

In this example let's wire everything from a `MonoBehaviour`. We build the world, add the system, initialize, and spawn one entity. Then `Tick()` drives the simulation each frame.

```csharp
using UnityEngine;
using Trecs;
using Unity.Mathematics;

public class GameLoop : MonoBehaviour
{
    World _world;

    void Start()
    {
        _world = new WorldBuilder()
            .AddTemplate(SpinnerEntity.Template)
            .AddSystem(new SpinnerSystem(speed: 2f))
            .Build();

        _world.Initialize();

        var accessor = _world.CreateAccessor(AccessorRole.Unrestricted);
        accessor.AddEntity<Spinner>()
            .Set(new Rotation { Value = quaternion.identity });
    }

    void Update()     => _world.Tick();
    void LateUpdate()   => _world.LateTick();
    void OnDestroy()  => _world.Dispose();
}
```

A few things to notice:

- `AddEntity<Spinner>()` takes a **tag**, not a template type. Trecs matches `Spinner` to `SpinnerEntity.Template` via the template's `IHasTags<Spinner>` declaration.
- The init accessor uses `AccessorRole.Unrestricted` because we're outside the tick loop. Inside systems, accessors are created automatically with the right role for that phase. See [Accessor Roles](advanced/accessor-roles.md).

## Where to Next

- **Start with [Core: World Setup](core/world-setup.md)** — the deeper reference for `WorldBuilder`, `WorldSettings`, lifecycle, and `WorldAccessor`. Read this next.
- [Systems](core/systems.md) — defining systems, iteration, and update phases.
- A complete runnable version of this walkthrough lives in **`Samples~/Tutorials/01_HelloEntity`** in the `com.trecs.core` package — install it via the Package Manager *Samples* tab.
- [Aspects](data-access/aspects.md) and [Queries & Iteration](data-access/queries-and-iteration.md) once you have multiple components per entity.
- The [Samples](samples/index.md) gallery — each one focuses on a single feature and has a companion doc.
