# 03 — Aspects

Type-safe component access bundles. Instead of declaring individual component parameters, aspects group related read/write access into a single struct.

**Source:** `Samples/03_Aspects/`

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
    public Position Position = Position.Default;
    public Velocity Velocity;
    public Speed Speed;
    public GameObjectId GameObjectId;
}
```

## Systems

### BoidMovementSystem

Defines an aspect and uses it for iteration:

```csharp
public partial class BoidMovementSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Boid boid)
    {
        boid.Position += World.FixedDeltaTime * boid.Speed * boid.Velocity;
    }

    partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
}
```

The `Boid` aspect provides:

- `ref readonly float3 Velocity` (read-only, unwrapped from `Velocity` component)
- `ref readonly float Speed` (read-only, unwrapped from `Speed` component)
- `ref float3 Position` (read-write, unwrapped from `Position` component)

### BoidWrapSystem

Wraps boids that go out of bounds. Uses `[ExecutesAfter]` to run after movement:

```csharp
[ExecutesAfter(typeof(BoidMovementSystem))]
public partial class BoidWrapSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Boid boid)
    {
        var p = boid.Position;
        if (p.x > _halfSize) p.x -= _halfSize * 2;
        else if (p.x < -_halfSize) p.x += _halfSize * 2;
        if (p.z > _halfSize) p.z -= _halfSize * 2;
        else if (p.z < -_halfSize) p.z += _halfSize * 2;
        boid.Position = p;
    }

    partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
}
```

### BoidRendererSystem (Variable Update)

Reads position and velocity to update the GameObject transform and face the movement direction.

## Concepts Introduced

- **Aspects** — `partial struct` implementing `IAspect`, `IRead<T>`, `IWrite<T>`
- **`[Unwrap]`** components expose their inner value type through aspect properties
- **Multiple aspects per system** — different systems can define different aspect views over the same components
- **Read vs Write** — `IRead<T>` provides `ref readonly`, `IWrite<T>` provides `ref`
