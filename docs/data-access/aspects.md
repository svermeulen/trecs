# Aspects

Aspects bundle related component access into a single reusable struct. Instead of declaring individual component parameters, you declare which components you read and write, and the source generator creates the access properties.

## Defining an Aspect

```csharp
partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
```

This generates properties:

- `ref readonly float3 Velocity` (read-only, unwrapped)
- `ref readonly float Speed` (read-only, unwrapped)
- `ref float3 Position` (read-write, unwrapped)
- `EntityIndex EntityIndex`

!!! note
    Components marked with `[Unwrap]` expose their inner field type directly (e.g., `float3` instead of `Position`). Non-unwrapped components expose the struct itself.

## Using Aspects in ForEachEntity

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

The aspect parameter is passed as `in` — the struct itself is read-only, but `IWrite` properties still provide mutable refs to the underlying components.

## Multiple IRead/IWrite Interfaces

Each interface supports up to 8 type parameters. Stack multiple interfaces for more components:

```csharp
partial struct ComplexView : IAspect,
    IRead<Position, Velocity, Speed, Health>,
    IWrite<Rotation, ColorComponent> { }
```

## Aspect Queries (Manual Iteration)

Every aspect gets a generated `Query()` method for manual iteration:

```csharp
partial struct ParticleView : IAspect, IRead<Position>, IWrite<Lifetime> { }

// Iterate with tag scope
foreach (var particle in ParticleView.Query(World).WithTags<SampleTags.Particle>())
{
    float3 pos = particle.Position;
    particle.Lifetime -= World.DeltaTime;
}

// Iterate all entities that have the aspect's components (regardless of tags)
foreach (var boid in Boid.Query(World).MatchByComponents())
{
    boid.Position += World.DeltaTime * boid.Speed * boid.Velocity;
}
```

This is useful when you need iteration logic in `Execute()` beyond what `[ForEachEntity]` supports (e.g., nested loops, conditional queries).

## Single Entity Access

```csharp
// Get aspect for a single entity from a query
var player = PlayerView.Query(World).WithTags<GameTags.Player>().Single();
```

## Aspects in Multiple Systems

Define aspects inside each system or share them. Since aspects are just partial structs, they can be defined wherever is most convenient:

```csharp
// Defined inside a system
public partial class PhysicsSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
    void Execute(in ActiveBall ball)
    {
        ball.Velocity += Gravity * World.DeltaTime;
    }

    partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
}

// Same system can have multiple aspects for different queries
public partial class RenderSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
    void RenderActive(in ActiveView ball) { ... }

    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
    void RenderResting(in RestingView ball) { ... }

    public void Execute()
    {
        RenderActive();
        RenderResting();
    }

    partial struct ActiveView : IAspect, IRead<Position, GameObjectId> { }
    partial struct RestingView : IAspect, IRead<Position, GameObjectId> { }
}
```

## AspectInterface

Use `[AspectInterface]` to define shared component access contracts that multiple aspects can implement:

```csharp
[AspectInterface]
public interface IMoveable : IRead<Position>, IWrite<Velocity> { }
```
