# Aspects

An aspect is a `partial struct` that bundles related component access into one reusable view. Declare which components it reads and writes; the source generator emits the access properties.

## Defining an aspect

```csharp
partial struct Boid : IAspect, IRead<Velocity, Speed>, IWrite<Position> { }
```

Assuming these components are marked `[Unwrap]` (single-field structs), this generates properties:

- `ref readonly float3 Velocity` (read-only, unwrapped inner type)
- `ref readonly float Speed` (read-only, unwrapped inner type)
- `ref float3 Position` (read-write, unwrapped inner type)

Without `[Unwrap]`, the property returns the wrapping struct itself (`ref Position` instead of `ref float3`).

Beyond component properties, the source generator emits:

- **`Remove(WorldAccessor)` / `Remove(NativeWorldAccessor)`** — schedules entity removal.
- **`SetTag<T>(WorldAccessor)` / `SetTag<T>(NativeWorldAccessor)`** — sets a tag on the entity.
- **`UnsetTag<T>(WorldAccessor)` / `UnsetTag<T>(NativeWorldAccessor)`** — unsets a tag.
- **`static Query(WorldAccessor)`** — returns a builder for manual iteration (see [below](#manual-aspect-queries)).
- **`Handle(WorldAccessor)` / `Handle(NativeWorldAccessor)`** — resolves to a stable `EntityHandle`.
- **`Boid(WorldAccessor, EntityHandle)` / `Boid(WorldAccessor, EntityIndex)`** — constructors for building an aspect view directly
- **`NativeFactory`** — nested struct for cross-entity aspect lookups in Burst jobs. Declare as a `[FromWorld]` field; call `factory.Create(entityIndex)` to construct the aspect. See [Advanced Jobs](../advanced/advanced-jobs.md).

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

The aspect is passed `in`. The struct is read-only, but `IWrite` properties still return mutable refs to the underlying components.

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

`Single()` / `TrySingle(out ...)` / `Count()` work too:

```csharp
var player = PlayerView.Query(World).WithTags<GameTags.Player>().Single();
```

## Where to define aspects

Aspects are partial structs and can live anywhere. Samples nest them as private `partial struct`s inside the system that uses them, since most aspects pair one-to-one with one system:

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
- [Sample 13 — Aspect Interfaces](../samples/13-aspect-interfaces.md): complete example.
