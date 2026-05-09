# Aspects

An aspect is a `partial struct` that bundles related component access into one reusable view. You declare which components the aspect reads and writes; the source generator emits the per-component access properties.

## Defining an aspect

```csharp
partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
```

This generates:

- `ref readonly float3 Velocity` (read-only, unwrapped)
- `ref readonly float Speed` (read-only, unwrapped)
- `ref float3 Position` (read-write, unwrapped)
- `EntityIndex EntityIndex`

A component marked `[Unwrap]` (single-field struct) exposes its inner value through the property. Without `[Unwrap]`, the property returns the wrapping struct (`ref Position` instead of `ref float3`).

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

The aspect is passed `in`. The struct itself is read-only, but `IWrite` properties still return mutable refs to the underlying components.

## Multiple `IRead` / `IWrite` interfaces

`IRead` and `IWrite` come in 1- to 8-arg generic overloads. To declare more, stack interfaces:

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

// Scope by the aspect's declared component types
foreach (var boid in Boid.Query(World).MatchByComponents())
{
    boid.Position += World.DeltaTime * boid.Speed * boid.Velocity;
}
```

Aspect queries don't auto-filter by the aspect's declared components. **Always supply scope**: `WithTags<…>()`, `MatchByComponents()`, or `InSet<…>()`.

`Single()` / `TrySingle(out ...)` work too:

```csharp
var player = PlayerView.Query(World).WithTags<GameTags.Player>().Single();
```

## Where to define aspects

Aspects are just partial structs — they can live anywhere. The convention in samples is to nest them as private `partial struct`s inside the system that uses them, since most aspects pair one-to-one with one system:

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

A system can declare multiple aspects — typically one per query.

## See also

- [Sample 03 — Aspects](../samples/03-aspects.md): a minimal aspect with `IRead` / `IWrite` parameters.
- [Aspect Interfaces](../advanced/aspect-interfaces.md): polymorphic helpers across multiple aspects sharing the same access surface.
- [Sample 15 — Aspect Interfaces](../samples/15-aspect-interfaces.md): worked example.
