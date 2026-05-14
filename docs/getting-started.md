# Getting Started

A five-minute walkthrough: one entity, one component, one system, ticking in a Unity scene.

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

`com.trecs.core` ships everything: the ECS runtime, deterministic binary serialization, snapshot / recording / playback, and the Trecs Player editor window. No separate serialization package to install.

## Your first entity

We'll build a spinning cube using these pieces:

- **Component** — the data
- **Tag** — what kind of entity this is
- **Template** — the blueprint (components and tags)
- **System** — the logic
- **World** — the runtime

### 1. Define a component

```csharp
using Unity.Mathematics;

public partial struct Rotation : IEntityComponent
{
    public quaternion Value;
}
```

`partial` is required — the source generator emits a companion file with `Equals`, `GetHashCode`, and equality operators.

### 2. Define a tag

Tags are empty marker structs that classify entities and act as query filters:

```csharp
public struct Spinner : ITag { }
```

### 3. Define a template

A template declares an entity kind — its tags (`ITagged<...>`) and its components (as fields):

```csharp
public partial class SpinnerEntity : ITemplate, ITagged<Spinner>
{
    Rotation Rotation;
}
```

The template class is never instantiated — the source generator reads it at compile time.

### 4. Write a system

`[ForEachEntity]` iterates every entity tagged with `Spinner`, calling this method for each:

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

`World` here is a source-generated property on the system — see [World Setup](core/world-setup.md).

### 5. Build and run

Wire it up from a `MonoBehaviour`:

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
            .BuildAndInitialize();

        var accessor = _world.CreateAccessor(AccessorRole.Unrestricted);
        accessor.AddEntity<Spinner>()
            .Set(new Rotation { Value = quaternion.identity });
    }

    void Update()    => _world.Tick();
    void LateUpdate()  => _world.LateTick();
    void OnDestroy() => _world.Dispose();
}
```

Two things to notice:

- `AddEntity<Spinner>()` takes the **tag**, not the template. Trecs matches `Spinner` to `SpinnerEntity` via `ITagged<Spinner>`.
- `AccessorRole.Unrestricted` is for setup outside the tick loop. Inside systems, the right role is wired automatically — see [Accessor Roles](advanced/accessor-roles.md).

## Where to next

- **[World Setup](core/world-setup.md)** — `WorldBuilder`, `WorldSettings`, lifecycle, `WorldAccessor`. Read this next.
- **[Systems](core/systems.md)** — defining systems, iteration, update phases.
- **[Aspects](data-access/aspects.md)** and **[Queries & Iteration](data-access/queries-and-iteration.md)** once you have multiple components per entity.
- **[Samples](samples/index.md)** — one feature each, with a companion doc. The runnable version of this walkthrough lives in `Samples~/Tutorials/01_HelloEntity` (install via the Package Manager *Samples* tab).
