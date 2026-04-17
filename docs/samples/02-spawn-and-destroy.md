# 02 — Spawn & Destroy

Dynamic entity creation and destruction. Spheres spawn at random positions, live for a set duration, then are removed along with their GameObjects.

**Source:** `Samples/02_SpawnAndDestroy/`

## What It Does

Colored spheres appear at random positions around the origin. Each has a countdown timer — when it expires, the entity and its GameObject are destroyed.

## Schema

### Components

```csharp
[Unwrap]
public partial struct Lifetime : IEntityComponent
{
    public float Value;
}
```

Plus `Position` and `GameObjectId` from Common.

### Tags & Template

```csharp
public struct Sphere : ITag { }

public partial class SphereEntity : ITemplate, IHasTags<SampleTags.Sphere>
{
    public Position Position = default;
    public Lifetime Lifetime;
    public GameObjectId GameObjectId;
}
```

## Systems

### SpawnSystem

Spawns a new sphere every 0.5 seconds at a random position with a random color:

```csharp
World.AddEntity<SampleTags.Sphere>()
    .Set(new Position(position))
    .Set(new Lifetime(_lifetime))
    .Set(_gameObjectRegistry.Register(go));
```

Uses `World.Rng` for deterministic random placement.

### LifetimeSystem

Counts down lifetime and removes expired entities, cleaning up their GameObjects inline:

```csharp
public partial class LifetimeSystem : ISystem
{
    readonly GameObjectRegistry _gameObjectRegistry;

    public LifetimeSystem(GameObjectRegistry gameObjectRegistry)
    {
        _gameObjectRegistry = gameObjectRegistry;
    }

    [ForEachEntity(Tags = new[] { typeof(SampleTags.Sphere) })]
    void Execute(in GameObjectId gameObjectId, ref Lifetime lifetime, EntityIndex entityIndex)
    {
        lifetime.Value -= World.DeltaTime;

        if (lifetime.Value <= 0)
        {
            var go = _gameObjectRegistry.Resolve(gameObjectId);
            Object.Destroy(go);
            _gameObjectRegistry.Unregister(gameObjectId);
            World.RemoveEntity(entityIndex);
        }
    }
}
```

### SphereRendererSystem (Variable Update)

Syncs position to GameObjects each visual frame.

## Concepts Introduced

- **Dynamic entity creation** with `AddEntity` and component initialization
- **Entity removal** with `RemoveEntity` (deferred until submission)
- **`World.Rng`** for deterministic random numbers
- **Individual component parameters** — `[ForEachEntity]` receives components directly
- **Inline cleanup** — destroying GameObjects at removal time inside the system
