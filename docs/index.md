# Trecs

A high-performance Entity Component System framework for Unity, designed for **deterministic simulation, recording/playback, and Burst/Jobs**.

## Why Trecs

- **Cache-friendly storage.** Components live in contiguous structure-of-arrays buffers grouped by tag set.
- **Small surface, lots of leverage.** Aspects bundle component access; sets give you sparse subsets without restructuring storage; templates declare entity blueprints with inheritance and partitions; pointers (`SharedPtr` / `UniquePtr`) let components reference data on the heap.
- **Burst & Jobs out of the box.** A source generator emits the job structs and chains the right `JobHandle` dependencies based on the components you read and write — no manual wiring.
- **Designed for determinism.** Fixed-timestep simulation, deterministic RNG, isolated input, and built-in snapshot / record / replay with desync detection.
- **Editor tooling.** A live entity inspector and a record / scrub / fork timeline window for diagnosing transient bugs.

## A taste

```csharp
// 1. Components — unmanaged structs holding per-entity data
public partial struct Position : IEntityComponent { public float3 Value; }
public partial struct Velocity : IEntityComponent { public float3 Value; }

// 2. A tag — a zero-cost marker
public struct PlayerTag : ITag { }

// 3. A template — the entity blueprint
public partial class PlayerEntity : ITemplate, ITagged<PlayerTag>
{
    Position Position;
    Velocity Velocity;
}

// 4. A system — logic that runs over matching entities
public partial class MovementSystem : ISystem
{
    [ForEachEntity(typeof(PlayerTag))]
    void Execute(ref Position position, in Velocity velocity)
    {
        position.Value += velocity.Value * World.DeltaTime;
    }
}

// 5. Build and run
var world = new WorldBuilder()
    .AddTemplate(PlayerEntity.Template)
    .AddSystem(new MovementSystem())
    .BuildAndInitialize();

// In a MonoBehaviour:
void Update()    => world.Tick();
void OnDestroy() => world.Dispose();
```

`World` inside a system body is a source-generated property — your access into the running world for that phase. See [Getting Started](getting-started.md) for the full walkthrough.

## Where to go next

- **[Getting Started](getting-started.md)** — install Trecs and run your first entity in a Unity scene.
- **[Core: World Setup](core/world-setup.md)** — the deeper reference for `WorldBuilder`, lifecycle, and `WorldAccessor`.
- **[Glossary](glossary.md)** — the terms (Group, Partition, Set, Tag, Aspect, Accessor, …) and how they relate.
- **[Samples](samples/index.md)** — a progressive tutorial series plus full sample games.
- **[FAQ](faq.md)** and **[Trecs vs Unity ECS](guides/trecs-vs-unity-ecs.md)** if you're sizing up the framework.
