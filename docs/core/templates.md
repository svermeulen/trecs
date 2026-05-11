# Templates

A template is the blueprint for an entity kind. It declares the components the entity carries and the tags that identify it.

## Defining a template

```csharp
public partial class SpinnerEntity : ITemplate, ITagged<SampleTags.Spinner>
{
    Rotation Rotation = new(quaternion.identity);  // Default value
    GameObjectId GameObjectId;                      // No default — must be set on AddEntity
}
```

A template is a `partial class` that:

1. Implements `ITemplate`
2. Declares **tags** via `ITagged<...>`
3. Declares **component fields** — the entity's data layout

Fields with default values supply fallback initialization. Fields without defaults must be set explicitly via `EntityInitializer.Set()` at creation.

> **Field visibility:** template fields must have **no access modifier** — write `Rotation Rotation;`, not `public Rotation Rotation;`. Template fields are a compile-time config DSL read by the source generator, not an API surface. Diagnostic `TRECS034` enforces this.

## Tags

Every template declares one or more tags:

```csharp
// Single tag
public partial class BulletEntity : ITemplate, ITagged<GameTags.Bullet>
{
    Position Position;
    Velocity Velocity;
}

// Multiple tags
public partial class PlayerBullet : ITemplate, ITagged<GameTags.Bullet, GameTags.Player>
{
    Position Position;
    Velocity Velocity;
    Damage Damage;
}
```

Systems and queries refer to entities by *tag*, not template class: `[ForEachEntity(typeof(GameTags.Enemy))]` and `World.CountEntitiesWithTags<GameTags.Enemy>()` name the tag. Runtime code shouldn't reference template classes directly — that keeps systems decoupled from concrete entity definitions.

A tag can play two roles:

- **1:1 with a template** — e.g. `GameTags.Spinner` is carried only by `SpinnerEntity`, so querying by it picks out that template's entities.
- **An abstract role across templates** — a `CommonTags.Renderable` tag declared on a base template is inherited by every template that does `IExtends<Renderable>`. Querying the role tag iterates every entity that fulfills it. This is Trecs's closest analogue to "interface" or "base class" polymorphism. (You can also add the role tag directly to each template instead of inheriting — same result.)

See [Tags](tags.md) and [Groups & TagSets](../advanced/groups-and-tagsets.md) for the storage details.

## Template inheritance

Use `IExtends<T>` to inherit components and tags from a base template:

```csharp
// Base
public partial class Renderable : ITemplate, ITagged<CommonTags.Renderable>
{
    Position Position;
    Rotation Rotation;
    UniformScale Scale;
    ColorComponent Color = new(Color.white);
}

// Extended — inherits everything from Renderable
public partial class FishEntity : ITemplate,
    IExtends<Renderable>,
    ITagged<FrenzyTags.Fish>
{
    Velocity Velocity;
    Speed Speed;
}
```

Multiple bases work too:

```csharp
public partial class ComplexEntity : ITemplate,
    IExtends<Renderable, Moveable>,
    ITagged<GameTags.Complex>
{
    Health Health;
}
```

When the same component appears in multiple bases:

- **Attributes must agree** — `[Interpolated]` on one base and `[VariableUpdateOnly]` on another is an error.
- **Defaults must match** — different defaults for the same component is an error.
- **One default is enough** — if only one base supplies a default, that default wins. The component becomes optional at the `AddEntity` call site.

```csharp
// Position in two bases — fine as long as attributes and defaults are compatible.
public partial class Renderable : ITemplate, ITagged<CommonTags.Renderable>
{
    Position Position = new(float3.zero);  // Has default
}

public partial class Moveable : ITemplate, ITagged<CommonTags.Moveable>
{
    Position Position;   // No default — OK, Renderable's default is used
    Velocity Velocity;
}

public partial class Player : ITemplate,
    IExtends<Renderable, Moveable>,
    ITagged<GameTags.Player>
{
    Health Health;
    // Inherits Position (with default from Renderable) and Velocity from Moveable
}
```

## Partitions

Partitions are mutually exclusive tag combinations a template can move between at runtime. Entities in different partitions are stored in separate contiguous arrays — useful when hot iteration over one state needs cache locality. Declare them with `IPartitionedBy<...>`.

### Presence/absence (binary)

Single-tag form: the tag is present or absent. Two partitions are emitted.

```csharp
public partial class BallEntity : ITemplate,
    ITagged<BallTags.Ball>,
    IPartitionedBy<BallTags.Active>
{
    Position Position;
    Velocity Velocity;
    RestTimer RestTimer;
    GameObjectId GameObjectId;
}
```

The absent partition has no name. Query it with `Without =`:

```csharp
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void UpdateActive(in ActiveBall ball) { /* ... */ }

[ForEachEntity(typeof(BallTags.Ball), Without = typeof(BallTags.Active))]
void UpdateResting(in RestingBall ball) { /* ... */ }
```

### Multi-variant

For three or more mutually exclusive states, list every variant:

```csharp
public partial class Enemy : ITemplate,
    ITagged<GameTags.Enemy>,
    IPartitionedBy<MoveState.Walking, MoveState.Running, MoveState.Idle>
{ /* ... */ }
```

The source generator emits one partition per variant.

### Multiple dimensions (cross product)

Each `IPartitionedBy<...>` is one independent dimension. Stack them and the source generator emits the cross product as concrete partitions — **O(N·k)** declarations yield **O(k^N)** partitions.

```csharp
public partial class Enemy : ITemplate,
    ITagged<GameTags.Enemy>,
    IPartitionedBy<HealthState.Alive, HealthState.Dead>,    // 2 variants
    IPartitionedBy<Visibility.Visible, Visibility.Hidden>,  // 2 variants
    IPartitionedBy<GameTags.Poisoned>                       // presence/absence
{ /* ... */ }
// → 8 partitions: every (Alive|Dead) × (Visible|Hidden) × (Poisoned-present|absent) combination.
```

!!! warning "Mind the explosion"
    Each declared dimension multiplies the partition count. Every partition is a distinct group with its own contiguous component buffer per component:

    | Dimensions  | Partitions |
    |-------------|------------|
    | 1 × binary  | 2          |
    | 2 × binary  | 4          |
    | 3 × binary  | 8          |
    | 4 × binary  | 16         |
    | 5 × binary  | 32         |
    | 6 × binary  | 64         |
    | 4-way + 3 × binary | 32  |

    **Rule of thumb: past 16 partitions, prefer [sets](../entity-management/sets.md).** Sets are presence/absence too but don't multiply — five "poisoned / stunned / burning / selected / targeted" sets are five sparse data structures, not 32 groups. The source generator emits `TRECS038` when a template crosses the 16-partition threshold.

    The cross product is worth it when dimensions are *intrinsic to the entity's storage shape* — e.g. an `Active` partition iterated every frame in a hot system. It's the wrong tool for states the design wants to name but the simulation rarely iterates by — those are cheaper as sets.

    **Lazy buffers.** Component arrays are allocated on first use per group, not at world-build time, so declaring partitions you rarely populate is cheap at startup. For the pre-0.x eager behavior, call `World.WarmupAllGroups()` after initialization, or `World.Warmup<TTag1, TTag2>(initialCapacity: N)` for a specific group about to be heavily populated.

### Transitions

Tag-change verbs handle moves between partitions; the runtime resolves the destination from the entity's current group plus the tag delta.

```csharp
// Presence/absence dim:
ball.SetTag<BallTags.Active>(World);    // start simulating
ball.UnsetTag<BallTags.Active>(World); // ground → idle

// Multi-variant dim:
enemy.SetTag<MoveState.Running>(World); // switch the active variant in MoveState's dim
```

`SetTag<T>` works for both shapes: in a presence/absence dim it turns the tag on; in a multi-variant dim it switches the active variant (other dimensions are preserved). `UnsetTag<T>` is only valid for presence/absence dims — multi-variant dims have no defined "absent" partition, so use `SetTag` to switch variants.

Multiple `SetTag` / `UnsetTag` calls on the same entity in one frame **coalesce**: changes on different dims merge into a single move at submission. Two ops on the *same* dim throw — this is a deliberate "no silent ordering" policy. To move an entity across several dims at once, just call `SetTag` for each:

```csharp
// Set Active on, switch MoveState to Running — one structural change at submit.
ball.SetTag<BallTags.Active>(World);
ball.SetTag<MoveState.Running>(World);
```

Partitions are an optimization — see [Entity Subset Patterns](../guides/entity-subset-patterns.md) for partitions vs. sets vs. component-value branching.

## Entity creation

```csharp
World.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42));
```

The tag arguments select the template. See [Entities](entities.md) for the full creation API.

## Global entity template

Extend the framework's global template to add world-wide components:

```csharp
public partial class MyGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    Score Score = default;
    DesiredFishCount DesiredFishCount = new() { Value = 100 };

    [Input(MissingInputBehavior.Retain)]
    MoveInput MoveInput = default;
}
```

The global entity is created automatically during world initialization — there is no `AddEntity` or `EntityInitializer`. **All fields in a global template must have explicit default values.**
