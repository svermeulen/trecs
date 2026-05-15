# 02 — Spawn & Destroy

Spheres spawn at random positions, live for a set duration, then are removed. GameObject lifetime is managed by a sample-side helper, not by the lifetime system itself.

**Source:** `com.trecs.core/Samples~/Tutorials/02_SpawnAndDestroy/`

## What it does

Coloured spheres appear at random positions around the origin. Each has a countdown timer — when it expires, the entity is removed. The companion GameObject is destroyed automatically via the shared `RenderableGameObjectManager` (in `Common/`) that watches for entities with `PrefabId` + `GameObjectId`.

## Schema

```csharp
[Unwrap]
public partial struct Lifetime : IEntityComponent
{
    public float Value;
}

public struct Sphere : ITag { }

public partial class SphereEntity
    : ITemplate,
        IExtends<CommonTemplates.RenderableGameObject>,
        ITagged<SampleTags.Sphere>
{
    Position Position = default;
    Lifetime Lifetime;
    ColorComponent Color = new(UnityEngine.Color.white);
    PrefabId PrefabId = new(SpawnAndDestroyPrefabs.Sphere);
}
```

`RenderableGameObject` is an abstract base template from `Common/` that adds the `PrefabId` and `GameObjectId` component fields. Concrete templates extend it and set `PrefabId` to a constant identifying the factory they registered with the manager.

## Systems

### SpawnSystem

Spawns a new sphere every 0.5 seconds with a random position and colour:

```csharp
World.AddEntity<SampleTags.Sphere>()
    .Set(new Position(position))
    .Set(new Lifetime(_lifetime))
    .Set(new ColorComponent(color));
```

Uses `World.Rng` for deterministic random placement. Notice the spawner never touches a GameObject — that's the manager's job once the entity submits.

### LifetimeSystem

Counts down the lifetime and removes expired entities:

```csharp
public partial class LifetimeSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Sphere))]
    void Execute(ref Lifetime lifetime, EntityAccessor entity)
    {
        lifetime.Value -= World.DeltaTime;

        if (lifetime.Value <= 0)
            entity.Remove();
    }
}
```

GameObject destruction is decoupled — the manager's `OnRemoved` observer (registered in `Common/RenderableGameObjectManager.cs`) tears the GO down at submission time.

### SpherePresenter (variable update)

Syncs `Position` and `ColorComponent` onto the GameObject each visual frame.

## Concepts introduced

- **Dynamic entity creation** with `AddEntity` and component initialization
- **Entity removal** with `entity.Remove()` (deferred until submission)
- **`World.Rng`** for deterministic random numbers — see [Time & RNG](../advanced/time-and-rng.md)
- **`PrefabId` + `GameObjectId` template pattern** — sample-side `RenderableGameObjectManager` handles GameObject lifecycle reactively via [Entity Events](../entity-management/entity-events.md). For an explicit `OnRemoved`-handler example doing the same thing in user code, see [Predator Prey](04-predator-prey.md).
- **Individual component parameters** — `[ForEachEntity]` receives components directly. See [Queries & Iteration](../data-access/queries-and-iteration.md).
