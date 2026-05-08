# 19 — Multiple Worlds

Two independent Trecs `World` instances running side-by-side in a single Unity scene. Demonstrates that worlds are isolated entity stores with their own systems, RNG, and lifecycle, while sharing the process-wide type ID registry.

**Source:** `Samples/19_MultipleWorlds/`

## What It Does

Two worlds tick every frame:

- **World A** — spawns red spheres around `x = -3`, every 0.5 s, lifetime 3 s.
- **World B** — spawns blue cubes around `x = +3`, every 0.7 s, lifetime 3 s.

Each world has its own `WorldBuilder`, `World`, system instances, and entity store. They are wired up in the same `Construct()` and ticked from the same `Bootstrap`, but otherwise know nothing about each other.

| Key | Action |
|---|---|
| 1 | Pause / resume World A |
| 2 | Pause / resume World B |

The on-screen HUD (`OnGUI`) shows each world's pause state and `WorldRegistry.ActiveWorlds.Count`, which should always read `2` while the scene is running.

## Schema

A single template registered with both worlds:

```csharp
public struct Critter : ITag { }

[Unwrap]
public partial struct Lifetime : IEntityComponent
{
    public float Value;
}

public partial class CritterEntity : ITemplate, IHasTags<SampleTags.Critter>
{
    Position Position = default;
    Lifetime Lifetime;
    GameObjectId GameObjectId;
}
```

`Position` and `GameObjectId` come from `Common/`.

## Composition Root

The interesting part — building two worlds in one `Construct()`:

```csharp
_worldA = new WorldBuilder()
    .SetDebugName("World A — Red Spheres")
    .AddEntityType(SampleTemplates.CritterEntity.Template)
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
    .AddEntityType(SampleTemplates.CritterEntity.Template)
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

Each world gets its own `SpawnSystem`/`LifetimeSystem`/`PrimitiveRendererSystem` instance — system instances are not shared across worlds. The `SetDebugName` call labels each world for editor tooling (e.g. the World dropdown in `TrecsPlayerWindow`).

The two worlds happen to register the *same template type* (`CritterEntity`) — that's allowed and common. Templates describe a shape; each world independently allocates per-group component arrays for that shape. An entity created in World A is not visible to any query in World B.

## Lifecycle Wiring

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

Pausing one world while the other keeps ticking is just "skip the call." The worlds maintain independent fixed-update accumulators, so a paused world resumes from where it left off — it doesn't try to "catch up" missed simulation time.

## Shared GameObjectRegistry

Both worlds share a single `GameObjectRegistry` instance. That's a deliberate choice: `GameObjectId` is just a process-wide integer handle into Unity's scene, and the registry has nothing to do with ECS isolation. Each world still has its own entity store, system instances, and accessors. Splitting the registry per-world would work too — the registry is application code, not Trecs.

## What's Process-Global vs Per-World

- **Per-world** — entity store, component arrays, system instances, accessors, RNG, blob cache, event manager, interpolation. Everything that holds simulation state.
- **Process-global** — component / tag / set type IDs (`ComponentTypeId<T>`, `Tag<T>`, `EntitySetId<T>`), plus the `WorldRegistry` that lists active worlds. These are stable identifiers, not state.

This split is what lets the same template type be registered in multiple worlds without conflict.

## Concepts Introduced

- **Multiple `World` instances in the same process** — supported and isolated.
- **`WorldBuilder.SetDebugName`** for editor disambiguation.
- **`WorldRegistry.ActiveWorlds`** for discovering all live worlds (useful for editor tooling).
- **Per-world pause** — the application chooses when to call `Tick()`; skipping the call is all "pause" means.
- **Shared application-side services** (here: `GameObjectRegistry`) across worlds, when the service has no ECS-level isolation requirement.
