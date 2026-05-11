<div class="hero" markdown>

<div class="hero-text" markdown>

# Trecs

A high-performance Entity Component System for Unity, built for **deterministic simulation, recording/playback, and Burst/Jobs**.

</div>

![Trecs](assets/logo.png)

</div>

## Why Trecs

- **Cache-friendly storage.** Components live in contiguous structure-of-arrays buffers grouped by tag set.
- **Small surface, lots of leverage.** Aspects bundle component access; sets give sparse subsets without restructuring storage; templates declare entity blueprints with inheritance and partitions; `SharedPtr` / `UniquePtr` let components reference heap data.
- **Burst & Jobs out of the box.** A source generator emits job structs and chains `JobHandle` dependencies from the components you read and write — no manual wiring.
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

`World` inside a system body is a source-generated property — your handle into the running world for that phase.

## Where to go next

<div class="grid cards" markdown>

-   :material-rocket-launch:{ .lg .middle } **[Getting Started](getting-started.md)**

    ---

    Install Trecs and run your first entity in a Unity scene.

-   :material-cube-outline:{ .lg .middle } **[Core: World Setup](core/world-setup.md)**

    ---

    Reference for `WorldBuilder`, lifecycle, and `WorldAccessor`.

-   :material-book-open-variant:{ .lg .middle } **[Glossary](glossary.md)**

    ---

    The terms — Group, Partition, Set, Tag, Aspect, Accessor — and how they relate.

-   :material-puzzle-outline:{ .lg .middle } **[Samples](samples/index.md)**

    ---

    A progressive tutorial series plus full sample games.

-   :material-help-circle-outline:{ .lg .middle } **[FAQ](faq.md)**

    ---

    Quick answers to common questions about scope, limits, and design choices.

-   :material-compare:{ .lg .middle } **[Trecs vs Unity ECS](guides/trecs-vs-unity-ecs.md)**

    ---

    Side-by-side comparison if you're sizing up the framework.

</div>
