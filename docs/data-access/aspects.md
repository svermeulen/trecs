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

Each `IRead` or `IWrite` interface supports up to 8 type parameters. If you need more, stack multiple interfaces:

```csharp
partial struct ComplexView : IAspect,
    IRead<Position, Velocity, Speed, Health>,
    IRead<Rotation, ColorComponent, Lifetime>,
    IWrite<UniformScale, Damage> { }
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

Aspect queries **do not auto-filter by the aspect's declared components** — you must explicitly call either `WithTags<…>()` to scope by tag, or `MatchByComponents()` to scope by the aspect's declared component types. Without one of these, the query has no group scope.

This is useful when you need iteration logic in `Execute()` beyond what `[ForEachEntity]` supports (e.g., iterating multiple queries at once) or if you just prefer this kind of style.

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
    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
    void Execute(in ActiveBall ball)
    {
        ball.Velocity += Gravity * World.DeltaTime;
    }

    partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
}

// Same system can have multiple aspects for different queries
public partial class RenderSystem : ISystem
{
    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
    void RenderActive(in ActiveView ball) { ... }

    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Resting))]
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

However, it is most common to define aspects as nested private structs inside the system that uses them, since they are typically specific to that system's logic.

## Aspect Interfaces (Shared Contracts) — advanced

Most aspects pair one-to-one with a single system, and don't need any sharing mechanism beyond C# itself. Aspect interfaces are for the rarer case where you want a helper method that works across several aspects: same shape of component access, different concrete aspect at each callsite. If you're reading this for the first time, you probably don't need it — skip on.

An aspect interface is a `partial interface` that extends `IAspect` and declares `IRead<>`/`IWrite<>` just like a concrete aspect struct. Any aspect struct that lists the interface in its base list inherits the declared components and implements the generated property contract.

```csharp
// Shared contract: anything that has a writable Position is an IPositionedBoid.
public partial interface IPositionedBoid : IAspect, IWrite<Position> { }

// Two concrete aspects that both implement the contract. Each can still declare
// additional components; IPositionedBoid only contributes Position.
partial struct MovementAspect : IPositionedBoid, IRead<Velocity, Speed> { }
partial struct WrapAspect     : IPositionedBoid { }

// Generic helper constrained on the aspect interface. Works on either aspect
// above — no boxing, no virtual dispatch.
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

**Rules:**

- The interface must be declared `partial` — the source generator attaches a partial with the ref-returning property stubs so generic helpers compile against it.
- The interface must list `IAspect` in its base list. That's the opt-in marker.
- Aspect interfaces cascade: an interface can extend another aspect interface, and all `IRead<>`/`IWrite<>` types are merged into the concrete aspect. Inheritance cycles between aspect interfaces are rejected by the C# compiler itself (CS0529).
- Iteration (`[ForEachEntity]`, `[SingleEntity]`) still requires a concrete aspect struct, not an aspect interface. Aspect interfaces are for polymorphic helpers you call *from* iteration, not as the iteration parameter itself.

