# Aspects

An aspect is a `partial struct` that bundles related component access into one reusable view. You declare which components the aspect reads and writes; the source generator emits the per-component access properties.

## Defining an aspect

```csharp
partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
```

This generates:

- `ref readonly float3 Velocity` (read-only, unwrapped — see below)
- `ref readonly float Speed` (read-only, unwrapped)
- `ref float3 Position` (read-write, unwrapped)
- `EntityIndex EntityIndex`

A component marked `[Unwrap]` (single-field struct) exposes its inner value directly. Without `[Unwrap]`, the property exposes the component struct itself (`ref Position` instead of `ref float3`).

## Using an aspect in `[ForEachEntity]`

```csharp
public partial class BoidMovementSystem : ISystem
{
    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Boid boid)
    {
        boid.Position += World.DeltaTime * boid.Speed * boid.Velocity;
    }

    partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
}
```

The aspect is passed as an `in` parameter. The struct itself is read-only, but `IWrite` properties still hand out mutable refs to the underlying components.

## Multiple `IRead` / `IWrite` interfaces

`IRead` and `IWrite` come in 1- to 8-arg generic overloads. To declare more than 8, stack interfaces:

```csharp
partial struct ComplexView : IAspect,
    IRead<Position, Velocity, Speed, Health>,
    IRead<Rotation, ColorComponent, Lifetime>,
    IWrite<UniformScale, Damage> { }
```

## Manual aspect queries

Every aspect gets a generated `Query()` method for iteration outside `[ForEachEntity]`:

```csharp
partial struct ParticleView : IAspect, IRead<Position>, IWrite<Lifetime> { }

foreach (var particle in ParticleView.Query(World).WithTags<SampleTags.Particle>())
{
    particle.Lifetime -= World.DeltaTime;
}

// Or scope by the aspect's declared component types.
foreach (var boid in Boid.Query(World).MatchByComponents())
{
    boid.Position += World.DeltaTime * boid.Speed * boid.Velocity;
}
```

Aspect queries **do not** auto-filter by the aspect's declared components. Always supply scope: `WithTags<…>()`, `MatchByComponents()`, or `InSet<…>()`.

`Single()` / `TrySingle(out …)` work too:

```csharp
var player = PlayerView.Query(World).WithTags<GameTags.Player>().Single();
```

## Where to define aspects

Aspects are just partial structs, so they can live anywhere. The convention in samples is to nest them as private `partial struct`s inside the system that uses them, since most aspects pair one-to-one with one system:

```csharp
public partial class PhysicsSystem : ISystem
{
    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
    void Execute(in ActiveBall ball)
    {
        ball.Velocity += Gravity * World.DeltaTime;
    }

    partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
}
```

A system can declare multiple aspects.  It's common to use one per query.

## Aspect interfaces (advanced)

For the rare case where you want a helper method that works across several aspects with the same component shape — same access surface, different concrete struct at each callsite. Most users won't need this; skip on first read.

An aspect interface is a `partial interface` that extends `IAspect` and lists `IRead<>` / `IWrite<>` like a concrete aspect:

```csharp
public partial interface IPositionedBoid : IAspect, IWrite<Position> { }

// Two aspects that satisfy the contract, each adding its own components.
partial struct MovementAspect : IPositionedBoid, IRead<Velocity, Speed> { }
partial struct WrapAspect     : IPositionedBoid { }

// Generic helper — no boxing, no virtual dispatch.
public static class BoidBounds
{
    public static void WrapPosition<T>(in T boid, float halfSize) where T : IPositionedBoid
    {
        ref var p = ref boid.Position;
        if (p.x >  halfSize) p.x -= halfSize * 2;
        if (p.x < -halfSize) p.x += halfSize * 2;
        if (p.z >  halfSize) p.z -= halfSize * 2;
        if (p.z < -halfSize) p.z += halfSize * 2;
    }
}
```

Rules:

- The interface must be `partial` and list `IAspect` in its base list.
- Aspect interfaces compose: one aspect interface can extend another, and all `IRead<>` / `IWrite<>` types are merged into the concrete aspect.
- Iteration entry points (`[ForEachEntity]`, `[SingleEntity]`) still require a concrete aspect struct. Aspect interfaces are for polymorphic helpers you call *from* iteration, not the iteration parameter itself.

See [sample 15 — Aspect Interfaces](../samples/15-aspect-interfaces.md) for a worked example.

## See also

- [Sample 03 — Aspects](../samples/03-aspects.md): a minimal aspect with `IRead` / `IWrite` parameters.
- [Sample 15 — Aspect Interfaces](../samples/15-aspect-interfaces.md): polymorphic helpers built on aspect interfaces.
