# 03 — Aspects

Bundled component access via aspects. Instead of declaring individual component parameters, aspects group related read/write operations into a single reusable struct.

**Source:** `com.trecs.core/Samples~/Tutorials/03_Aspects/`

## What It Does

Boids (simple agents) move in straight lines and wrap around the edges of a bounded area. Their GameObjects are rotated to face the direction of movement.

## Schema

### Components

`Position`, `Velocity`, `Speed`, `GameObjectId` from Common.

### Tags & Template

```csharp
public struct Boid : ITag { }

public partial class BoidEntity : ITemplate, IHasTags<SampleTags.Boid>
{
    Position Position = default;
    Velocity Velocity;
    Speed Speed;
    GameObjectId GameObjectId;
}
```

## Systems

### BoidMovementSystem

Defines an aspect and uses it for iteration:

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

Wraps boids that go out of bounds. Uses `[ExecuteAfter]` to run after movement:

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

### BoidRendererSystem (Variable Update)

Reads position and velocity to update the GameObject transform and face the movement direction.

## Concepts Introduced

- **Aspects** — `partial struct` implementing `IAspect`, `IRead<T>`, `IWrite<T>`. See [Aspects](../data-access/aspects.md).
- **`[Unwrap]`** components expose their inner value type through aspect properties. See [Components](../core/components.md).
- **Multiple aspects per system** — different systems can define different aspect views over the same components.
- **Read vs Write** — `IRead<T>` provides `ref readonly`, `IWrite<T>` provides `ref`.
- **`Aspect.Query(World).MatchByComponents()`** vs **`[ForEachEntity]`** — two ways to drive iteration over an aspect. See [Queries & Iteration](../data-access/queries-and-iteration.md).
