# 03 — Aspects

Aspects group related read/write component operations into a single reusable struct, instead of listing individual component parameters.

**Source:** `com.trecs.core/Samples~/Tutorials/03_Aspects/`

## What it does

Boids move in straight lines and wrap around a bounded area, rotated to face their movement direction.

## Schema

### Components

`Position`, `Velocity`, `Speed`, `GameObjectId` from Common.

### Tags & template

```csharp
public struct Boid : ITag { }

public partial class BoidEntity : ITemplate, ITagged<SampleTags.Boid>
{
    Position Position = default;
    Velocity Velocity;
    Speed Speed;
    GameObjectId GameObjectId;
}
```

## Systems

### BoidMovementSystem

Defines an aspect and iterates over it:

```csharp
public partial class BoidMovementSystem : ISystem
{
    public void Execute()
    {
        foreach (var boid in Boid.Query(World).MatchByComponents())
        {
            boid.Position += World.DeltaTime * boid.Speed * boid.Velocity;
        }
    }

    partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
}
```

The `Boid` aspect provides:

- `ref readonly float3 Velocity` (read-only, unwrapped from `Velocity` component)
- `ref readonly float Speed` (read-only, unwrapped from `Speed` component)
- `ref float3 Position` (read-write, unwrapped from `Position` component)

### BoidWrapSystem

Wraps boids that go out of bounds. `[ExecuteAfter]` ensures it runs after movement:

```csharp
[ExecuteAfter(typeof(BoidMovementSystem))]
public partial class BoidWrapSystem : ISystem
{
    readonly float _halfSize;

    public BoidWrapSystem(float areaSize)
    {
        _halfSize = areaSize / 2f;
    }

    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Boid boid)
    {
        ref var p = ref boid.Position;

        if (p.x > _halfSize) p.x -= _halfSize * 2;
        else if (p.x < -_halfSize) p.x += _halfSize * 2;
        if (p.z > _halfSize) p.z -= _halfSize * 2;
        else if (p.z < -_halfSize) p.z += _halfSize * 2;
    }

    partial struct Boid : IAspect, IWrite<Position> { }
}
```

### BoidRendererSystem (variable update)

Reads position and velocity, then updates the GameObject transform to face the movement direction.

## Concepts introduced

- **Aspects** — `partial struct` implementing `IAspect`, `IRead<T>`, `IWrite<T>`. See [Aspects](../data-access/aspects.md).
- **`[Unwrap]`** components expose their inner value type through aspect properties. See [Components](../core/components.md).
- **Multiple aspects per system** — different systems can define different aspect views over the same components.
- **Read vs Write** — `IRead<T>` provides `ref readonly`, `IWrite<T>` provides `ref`.
- **`Aspect.Query(World).MatchByComponents()`** vs **`[ForEachEntity]`** — two ways to drive iteration over an aspect. See [Queries & Iteration](../data-access/queries-and-iteration.md).
