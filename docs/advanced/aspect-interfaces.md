# Aspect Interfaces

An aspect interface lets a helper method work across several aspects that share the same component access shape — same read/write surface, but a different concrete struct at each call site.

An aspect interface is a `partial interface` that extends `IAspect` and lists `IRead<>` / `IWrite<>` like a concrete aspect:

```csharp
public partial interface IBoid : IAspect, IWrite<Position>, IRead<Velocity> { }

// Two aspects that satisfy the contract, each adding its own extras.
partial struct MovementAspect : IBoid, IWrite<Acceleration> { }
partial struct WrapAspect     : IBoid { }

// Generic helper — no boxing, no virtual dispatch.
public static class BoidIntegration
{
    public static void StepAndWrap<T>(in T boid, float halfSize, float dt) where T : IBoid
    {
        ref var p = ref boid.Position;
        p += boid.Velocity * dt;
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

See [sample 14 — Aspect Interfaces](../samples/14-aspect-interfaces.md) for a complete example.
