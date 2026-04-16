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
    public Position Position = Position.Default;
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
    .Set(_gameObjectRegistry.Register(go))
    .AssertComplete();
```

Uses `World.Rng` for deterministic random placement.

### LifetimeSystem

Counts down lifetime and removes expired entities:

```csharp
[ExecutesAfter(typeof(SpawnSystem))]
public partial class LifetimeSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(SampleTags.Sphere) })]
    void Execute(in SphereView sphere)
    {
        sphere.Lifetime -= World.DeltaTime;

        if (sphere.Lifetime <= 0)
        {
            World.RemoveEntity(sphere.EntityIndex);
        }
    }

    partial struct SphereView : IAspect, IRead<GameObjectId>, IWrite<Lifetime> { }
}
```

### SphereRendererSystem (Variable Update)

Syncs position to GameObjects each visual frame.

## Concepts Introduced

- **Dynamic entity creation** with `AddEntity` and component initialization
- **Entity removal** with `RemoveEntity` (deferred until submission)
- **`[ExecutesAfter]`** for explicit system ordering
- **`World.Rng`** for deterministic random numbers
- **Aspects** for bundling read/write access (`IRead<GameObjectId>, IWrite<Lifetime>`)
- **Event handlers** for cleanup when entities are removed (destroying GameObjects)
