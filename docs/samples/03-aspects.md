# 03 — Aspects

Bundled component access via aspects. Instead of declaring individual component parameters, aspects group related read/write operations into a single reusable struct.

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

### Shared contract: `IPositionedBoid`

Both `BoidMovementSystem.Boid` and `BoidWrapSystem.Boid` write `Position`. Rather than letting each system duplicate position-manipulation logic, the sample factors the "has-a-writable-Position" part out into an **aspect interface** — a shared component-access contract multiple aspects can implement:

```csharp
public partial interface IPositionedBoid : IAspect, IWrite<Position> { }

public static class BoidBounds
{
    public static void WrapPosition<T>(in T boid, float halfSize) where T : IPositionedBoid
    {
        ref var p = ref boid.Position;
        if (p.x > halfSize)      p.x -= halfSize * 2;
        else if (p.x < -halfSize) p.x += halfSize * 2;
        if (p.z > halfSize)      p.z -= halfSize * 2;
        else if (p.z < -halfSize) p.z += halfSize * 2;
    }
}
```

The helper is a single static method constrained on the interface. It compiles and inlines for any aspect that implements `IPositionedBoid` — no boxing, no virtual dispatch.

### BoidWrapSystem

Wraps boids that go out of bounds. Uses `[ExecutesAfter]` to run after movement, and delegates the wrap logic to the shared helper:

```csharp
[ExecutesAfter(typeof(BoidMovementSystem))]
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
        BoidBounds.WrapPosition(boid, _halfSize);
    }

    partial struct Boid : IPositionedBoid { }
}
```

`BoidMovementSystem.Boid` also implements `IPositionedBoid`, so it's eligible for any helper written against the interface — `BoidBounds.WrapPosition` is just the first one.

### BoidRendererSystem (Variable Update)

Reads position and velocity to update the GameObject transform and face the movement direction.

## Concepts Introduced

- **Aspects** — `partial struct` implementing `IAspect`, `IRead<T>`, `IWrite<T>`
- **Aspect interfaces** — `partial interface` extending `IAspect` plus `IRead<>`/`IWrite<>`; lets multiple aspects share a component-access contract so a generic helper can operate on any of them
- **`[Unwrap]`** components expose their inner value type through aspect properties
- **Multiple aspects per system** — different systems can define different aspect views over the same components
- **Read vs Write** — `IRead<T>` provides `ref readonly`, `IWrite<T>` provides `ref`
