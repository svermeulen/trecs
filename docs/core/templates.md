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

A tag can be 1:1 with a template (e.g. `GameTags.Spinner` carried only by `SpinnerEntity`) or shared across many templates as a role (a `CommonTags.Renderable` tag declared on a base template, inherited by every template that extends it). See [Tags](tags.md) and [Groups & TagSets](../advanced/groups-and-tagsets.md) for the storage details.

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

## Partitions

Partitions are mutually exclusive tag combinations a template can move between at runtime. Entities in different partitions are stored in separate contiguous arrays — useful when a hot iteration over one state (`Active`) needs to be cache-friendly:

```csharp
public partial class BallEntity : ITemplate,
    ITagged<BallTags.Ball>,
    IHasPartition<BallTags.Active>,
    IHasPartition<BallTags.Resting>
{
    Position Position;
    Velocity Velocity;
    RestTimer RestTimer;
    GameObjectId GameObjectId;
}
```

Each `IHasPartition` declares a valid partition. The entity always carries the base tag (`BallTags.Ball`) plus one of the partition tag sets.

### Transitions

Move an entity between partitions with `MoveTo`:

```csharp
// Hits the ground → Resting
World.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);

// Or via an aspect
ball.MoveTo<BallTags.Ball, BallTags.Active>(World);
```

Systems target specific partitions like any other tag combination:

```csharp
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void UpdateActive(in ActiveBall ball) { /* ... */ }

[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Resting))]
void UpdateResting(in RestingBall ball) { /* ... */ }
```

Partitions are an optimization — see [Entity Subset Patterns](../recipes/entity-subset-patterns.md) for when to reach for partitions vs. sets vs. component-value branching.

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

    [Input(MissingInputBehavior.RetainCurrent)]
    MoveInput MoveInput = default;
}
```

The global entity is created automatically during world initialization — there is no `AddEntity` or `EntityInitializer`. Because of this, **all fields in a global template must have explicit default values**.
