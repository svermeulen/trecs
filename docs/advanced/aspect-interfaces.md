# Aspect Interfaces

For helper methods that work across several aspects sharing the same component shape — same access surface, different concrete struct per callsite.

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
- Aspect interfaces compose: one can extend another, and all `IRead<>` / `IWrite<>` types merge into the concrete aspect.
- Iteration entry points (`[ForEachEntity]`, `[SingleEntity]`) still require a concrete aspect struct. Aspect interfaces are for polymorphic helpers called *from* iteration, not the iteration parameter itself.

See [sample 15 — Aspect Interfaces](../samples/15-aspect-interfaces.md) for a complete example.
