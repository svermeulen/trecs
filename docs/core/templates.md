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

> **No access modifier on fields.** Write `Rotation Rotation;`, not `public Rotation Rotation;`. Template fields configure the source generator at compile time; they aren't a runtime API surface. Diagnostic `TRECS034` flags violations.

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
    ColorComponent Color = new(UnityEngine.Color.white);
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

### Abstract templates

A template marked `abstract` exists only to be extended — `WorldBuilder.AddTemplate` refuses to register it. Use this when a template is a role or mixin, not a concrete entity shape.

```csharp
public abstract partial class Renderable : ITemplate, ITagged<CommonTags.Renderable>
{
    Position Position;
    Rotation Rotation;
    UniformScale Scale;
}

public partial class FishEntity : ITemplate, IExtends<Renderable>, ITagged<FrenzyTags.Fish> { /* ... */ }

builder.AddTemplate(FishEntity.Template);     // OK
builder.AddTemplate(Renderable.Template);     // TRECS039: abstract template
```

Note that you can still extend non-abstract templates.  This keyword is just used to communicate to reader that it is abstract and also to prevent use inside `WorldBuilder.AddTemplate`.

### Multiple bases

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

Partitions are mutually exclusive tag combinations an entity can move between at runtime. Entities in different partitions are stored in separate contiguous arrays — useful when hot iteration over one state needs cache locality. Declare them with `IPartitionedBy<...>`.

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

Use `Withouts = new[] { typeof(A), typeof(B) }` for multiple exclusions.

### Multi-variant

For two or more named variants (no implicit "absent" partition), list each one:

```csharp
public partial class Enemy : ITemplate,
    ITagged<GameTags.Enemy>,
    IPartitionedBy<MoveState.Walking, MoveState.Running, MoveState.Idle>
{ /* ... */ }
```

The source generator emits one partition per variant.

### Multiple dimensions (cross product)

Each `IPartitionedBy<...>` is one independent dimension. Stack them and the generator emits one partition per combination — three binary dims, for example, gives **2 × 2 × 2 = 8** partitions.

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

    **Rule of thumb: Before reaching for partitions, consider using the more lightweight [sets](../entity-management/sets.md).** Sets are presence/absence too but don't multiply — five "poisoned / stunned / burning / selected / targeted" sets are five sparse data structures, not 32 groups.

    Partitions are a cache-locality optimization: use them when a hot system iterates one variant every frame and benefits from those entities being packed contiguously — e.g. physics over `Active` balls. States the design wants to *name* but rarely *iterates by* are cheaper as sets.

### Transitions

Tag-change verbs handle moves between partitions; the runtime resolves the destination from the entity's current group plus the tag delta.

```csharp
// Presence/absence dim:
ball.SetTag<BallTags.Active>(World);    // start simulating
ball.UnsetTag<BallTags.Active>(World); // ground → idle

// Multi-variant dim:
enemy.SetTag<MoveState.Running>(World); // switch the active variant in MoveState's dim
```

`SetTag<T>` works on both partition kinds:

- **Binary (presence/absence)** — turns the tag on. `UnsetTag<T>` turns it off.
- **Multi-variant** — switches the entity to that variant of the dimension, leaving other dimensions unchanged. `UnsetTag<T>` doesn't apply (there's no "off" state) — switch variants with another `SetTag<T>` call.

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

The global entity is created automatically during world initialization — there is no `AddEntity` or `EntityInitializer`. For this reason, **all fields in a global template must have explicit default values.**
