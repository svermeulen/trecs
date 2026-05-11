# 19 — Multiple Worlds

Two independent Trecs `World` instances running side-by-side in one Unity scene. Worlds are isolated entity stores with their own systems, RNG, and lifecycle, while sharing the process-wide type ID registry.

**Source:** `Samples/19_MultipleWorlds/`

## What it does

Two worlds tick every frame:

- **World A** — spawns red spheres around `x = -3`, every 0.5 s, lifetime 3 s.
- **World B** — spawns blue cubes around `x = +3`, every 0.7 s, lifetime 3 s.

Each world has its own `WorldBuilder`, `World`, system instances, and entity store. They share a `Construct()` and `Bootstrap` but otherwise know nothing about each other.

| Key | Action |
|---|---|
| 1 | Pause / resume World A |
| 2 | Pause / resume World B |

The HUD (`OnGUI`) shows each world's pause state and `WorldRegistry.ActiveWorlds.Count`, which reads `2` while the scene is running.

## Schema

A single template registered with both worlds:

```csharp
public struct Critter : ITag { }

[Unwrap]
public partial struct Lifetime : IEntityComponent
{
    public float Value;
}

public partial class CritterEntity : ITemplate, ITagged<SampleTags.Critter>
{
    Position Position = default;
    Lifetime Lifetime;
    GameObjectId GameObjectId;
}
```

`Position` and `GameObjectId` come from `Common/`.

## Composition root

Building two worlds in one `Construct()`:

```csharp
_worldA = new WorldBuilder()
    .SetDebugName("World A — Red Spheres")
    .AddTemplate(SampleTemplates.CritterEntity.Template)
    .Build();

_worldA.AddSystems(new ISystem[]
{
    new SpawnSystem(SpawnIntervalA, Lifetime, SpawnRadius,
                    new Vector3(-WorldSeparation, 0, 0),
                    Color.red, PrimitiveType.Sphere, gameObjectRegistry),
    new LifetimeSystem(gameObjectRegistry),
    new PrimitiveRendererSystem(gameObjectRegistry),
});

_worldB = new WorldBuilder()
    .SetDebugName("World B — Blue Cubes")
    .AddTemplate(SampleTemplates.CritterEntity.Template)
    .Build();

_worldB.AddSystems(new ISystem[]
{
    new SpawnSystem(SpawnIntervalB, Lifetime, SpawnRadius,
                    new Vector3(WorldSeparation, 0, 0),
                    Color.blue, PrimitiveType.Cube, gameObjectRegistry),
    new LifetimeSystem(gameObjectRegistry),
    new PrimitiveRendererSystem(gameObjectRegistry),
});
```

Each world gets its own `SpawnSystem`/`LifetimeSystem`/`PrimitiveRendererSystem` instance — system instances are not shared across worlds. `SetDebugName` labels each world for editor tooling (e.g. the World dropdown in `TrecsPlayerWindow`).

Both worlds register the *same template type* (`CritterEntity`) — allowed and common. Templates describe a shape; each world independently allocates per-group component arrays for that shape. An entity created in World A is invisible to queries in World B.

## Lifecycle wiring

Two worlds means two of every lifecycle hook:

```csharp
initializables = new() { _worldA.Initialize, _worldB.Initialize };

tickables = new()
{
    ReadInput,                                        // pause toggles
    () => { if (!_pausedA) _worldA.Tick(); },
    () => { if (!_pausedB) _worldB.Tick(); },
};

lateTickables = new()
{
    () => { if (!_pausedA) _worldA.LateTick(); },
    () => { if (!_pausedB) _worldB.LateTick(); },
};

disposables = new() { _worldA.Dispose, _worldB.Dispose };
```

Pausing one world while the other ticks is "skip the call." Worlds maintain independent fixed-update accumulators, so a paused world resumes where it left off — it doesn't try to catch up missed simulation time.

## Shared GameObjectRegistry

Both worlds share one `GameObjectRegistry`. Deliberate: `GameObjectId` is a process-wide integer handle into Unity's scene, and the registry has nothing to do with ECS isolation. Each world still has its own entity store, system instances, and accessors. Splitting the registry per-world would also work — the registry is application code, not Trecs.

## What's process-global vs per-world

- **Per-world** — entity store, component arrays, system instances, accessors, RNG, blob cache, event manager, interpolation. Everything holding simulation state.
- **Process-global** — component / tag / set type IDs (`ComponentTypeId<T>`, `Tag<T>`, `EntitySetId<T>`), plus the `WorldRegistry` listing active worlds. Stable identifiers, not state.

This split is what lets the same template type register in multiple worlds without conflict.

## Concepts introduced

- **Multiple `World` instances in one process** — supported and isolated.
- **`WorldBuilder.SetDebugName`** for editor disambiguation.
- **`WorldRegistry.ActiveWorlds`** for discovering all live worlds (useful for editor tooling).
- **Per-world pause** — the application chooses when to call `Tick()`; skipping the call is all "pause" means.
- **Shared application-side services** (here: `GameObjectRegistry`) across worlds, when the service has no ECS-level isolation requirement.
