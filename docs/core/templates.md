# Templates

A template is the blueprint for an entity kind — it declares the components an entity carries and the tags that identify it.

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

Fields with default values supply fallback initialization. Fields without defaults must be set explicitly via `EntityInitializer.Set()` when the entity is created.

> **Field visibility:** template fields must have **no access modifier** — write `Rotation Rotation;`, not `public Rotation Rotation;`. Template fields are a config DSL read by the source generator at compile time, not an API surface. The compiler enforces this with diagnostic `TRECS034`.

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

Tags are how systems and queries refer to entities at runtime: `[ForEachEntity(typeof(GameTags.Enemy))]` and `World.CountEntitiesWithTags<GameTags.Enemy>()` name the *tag*, not the template class. Runtime code shouldn't reference template classes directly — that keeps systems decoupled from concrete entity definitions.

A tag can play two distinct roles:

- **1:1 with a template** — e.g. `GameTags.Spinner` is carried only by `SpinnerEntity`, so querying by it picks out exactly that template's entities.
- **An abstract role across templates** — a `CommonTags.Renderable` tag declared on a base template is inherited by every template that does `IExtends<Renderable>`. Querying by the role tag iterates every entity that fulfills it. This is Trecs's closest analogue to "interface" or "base class" polymorphism. (You can also add the role tag directly to each template instead of using inheritance — same result.)

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

- **Attributes must agree** — declaring `[Interpolated]` on one base and `[VariableUpdateOnly]` on another is an error.
- **Defaults must match** — different defaults for the same component is an error.
- **One default is enough** — if only one base supplies a default, that default is used. The component becomes optional at the `AddEntity` call site.

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

Partitions are mutually exclusive tag combinations a template can move between at runtime. Entities in different partitions are stored in separate contiguous arrays — useful when a hot iteration over one state needs to be cache-friendly. Declare them with `IPartitionedBy<...>`.

### Presence/absence (binary)

Single-tag form: the tag is either present or absent. Two partitions are emitted automatically.

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

There is no companion `Inactive` / `Resting` tag — the absent partition has no name. Query it with `Without =`:

```csharp
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void UpdateActive(in ActiveBall ball) { /* ... */ }

[ForEachEntity(typeof(BallTags.Ball), Without = typeof(BallTags.Active))]
void UpdateResting(in RestingBall ball) { /* ... */ }
```

### Multi-variant

For dimensions with three or more mutually exclusive states, list every variant:

```csharp
public partial class Enemy : ITemplate,
    ITagged<GameTags.Enemy>,
    IPartitionedBy<MoveState.Walking, MoveState.Running, MoveState.Idle>
{ /* ... */ }
```

The source generator emits one partition per variant.

### Multiple dimensions (cross product)

Each `IPartitionedBy<...>` is one independent dimension. Stack them and the source generator emits the cross product as concrete partitions automatically — authors write **O(N·k)** declarations and get **O(k^N)** partitions.

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
    Each declared dimension multiplies the partition count. The cost compounds — every partition is a distinct group with its own contiguous component buffer per component:

    | Dimensions  | Partitions |
    |-------------|------------|
    | 1 × binary  | 2          |
    | 2 × binary  | 4          |
    | 3 × binary  | 8          |
    | 4 × binary  | 16         |
    | 5 × binary  | 32         |
    | 6 × binary  | 64         |
    | 4-way + 3 × binary | 32  |

    **Rule of thumb: past 16 partitions, prefer [sets](../entity-management/sets.md).** Sets are presence/absence too but don't multiply — five "poisoned / stunned / burning / selected / targeted" sets are five sparse data structures, not 32 groups. The source generator emits a `TRECS038` warning when a template crosses the 16-partition threshold.

    The cross product is worth it when the dimensions are *intrinsic to the entity's storage shape* — e.g. an `Active` partition you iterate every frame in a hot system, where cache locality matters. It's the wrong tool for "states the design wants to name but the simulation rarely iterates by," which are almost always cheaper as sets.

    **Lazy buffers.** Component arrays are allocated on first use per group, not at world-build time — so declaring partitions you rarely populate is cheap at startup. If you want the pre-0.x eager behavior (everything pre-allocated up front), call `World.WarmupAllGroups()` after initialization, or `World.Warmup<TTag1, TTag2>(initialCapacity: N)` for a specific group you know is about to be heavily populated.

### Transitions

Tag-change verbs handle moves between partitions; the runtime resolves the destination from the entity's current group plus the tag delta.

```csharp
// Presence/absence dim:
ball.AddTag<BallTags.Active>(World);    // start simulating
ball.RemoveTag<BallTags.Active>(World); // ground → idle

// Multi-variant dim (also valid for presence/absence):
enemy.SetTag<MoveState.Running>(World); // switch the active variant in MoveState's dim
```

`SetTag<T>` and `AddTag<T>` are aliases — `SetTag` reads more naturally for variant dims (a "switch"), `AddTag` for presence/absence (a "turn on"). `RemoveTag<T>` is only valid for presence/absence dims; for multi-variant dims there is no defined "absent" partition, so use `SetTag`/`AddTag` to switch instead.

For a fully-specified destination, the runtime form `World.MoveTo(entityIndex, tagSet)` still works.

Partitions are an optimization — see [Entity Subset Patterns](../guides/entity-subset-patterns.md) for when to reach for partitions vs. sets vs. component-value branching.

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

The global entity is created automatically during world initialization — there is no `AddEntity` or `EntityInitializer`. Because of this, **all fields in a global template must have explicit default values**.
