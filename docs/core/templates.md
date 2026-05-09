# Templates

Templates define the component layout and tag identity of an entity — like a blueprint.

## Defining a Template

```csharp
public partial class SpinnerEntity : ITemplate, ITagged<SampleTags.Spinner>
{
    Rotation Rotation = new(quaternion.identity);  // Default value
    GameObjectId GameObjectId;                      // No default (must be set during AddEntity)
}
```

A template is a `partial class` that:

1. Implements **`ITemplate`**
2. Declares **tags** via `ITagged<T1, ...>`
3. Declares **component fields** — the entity's data layout

Fields with default values supply fallback initialization. Fields without defaults must be set explicitly via `EntityInitializer.Set()` when the entity is created.

> **Field visibility:** template fields must be declared with **no access modifier** — write `Rotation Rotation;`, not `public Rotation Rotation;`. Template fields are a config DSL read by the source generator at compile time, not an API surface. The compiler enforces this with diagnostic `TRECS034`.

## Tags

Every template declares one or more tags via `ITagged`:

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

See [Tags](tags.md) for details on how tags are used.

## Templates and Tags: Who Does What

Templates and tags are closely related but play distinct roles. Understanding the split is key to using Trecs idiomatically:

- **A template is the concrete shape.** It declares the exact component layout an entity is spawned with — defaults, partitions, and which tags it carries. The template class is referenced when you *define* the entity kind (the partial class itself), when you *register* it with the builder (`AddTemplate(EnemyEntity.Template)`), and when other templates extend it via `IExtends<EnemyEntity>`.
- **A tag is the identity handle the rest of the code uses.** Systems, queries, aspects, and events all refer to entities through their tags, not through their template class. `[ForEachEntity(typeof(GameTags.Enemy))]` and `World.CountEntitiesWithTags<GameTags.Enemy>()` are the normal way to talk about "enemies" — runtime code should not name the template class directly. This is important to keep code loosely coupled, testable, and re-usable across different application configurations.

### Tags as Template Identity

Because every template declares at least one identity tag, tags effectively become the runtime way to identify which template an entity belongs to:

- **1:1 with a concrete template** — `GameTags.Spinner` is carried only by `SpinnerEntity`, so querying by it picks out exactly that template's entities.
- **An abstract role shared across templates** — a `CommonTags.Renderable` tag declared on a base template is inherited by every template that does `IExtends<Renderable>`. Querying by the role tag iterates every entity that fulfills it. This is Trecs's closest analogue to "interface" or "base class" polymorphism.  Alternatively, you can add `CommonTags.Renderable` directly to each template instead of using template inheritance for the same result.

See [Tags](tags.md) for the mechanics, and [Groups, GroupIndex & TagSets](../advanced/groups-and-tagsets.md) for how tag combinations map to storage.

## Template Inheritance

Use `IExtends<T>` to inherit components and tags from a base template:

```csharp
// Base template
public partial class Renderable : ITemplate, ITagged<CommonTags.Renderable>
{
    Position Position;
    Rotation Rotation;
    UniformScale Scale;
    ColorComponent Color = new(Color.white);
}

// Extended template — inherits Position, Rotation, Scale, Color
public partial class FishEntity : ITemplate,
    IExtends<Renderable>,
    ITagged<FrenzyTags.Fish>
{
    Velocity Velocity;
    Speed Speed;
}
```

Multiple inheritance is supported

```csharp
public partial class ComplexEntity : ITemplate,
    IExtends<Renderable, Moveable>,
    ITagged<GameTags.Complex>
{
    Health Health;
}
```

### How Inherited Definitions Are Merged

When a template extends multiple bases, all components, tags, and partitions are merged together:

- **Components** — The union of all components from all bases and the concrete template. If the same component appears in multiple bases, the declarations are merged as long as their attributes and defaults are compatible (see below).
- **Tags** — Combined into a union set. Duplicates are deduplicated automatically.
- **Partitions** — Combined from all bases.

When the same component appears in more than one base template:

- **Attributes must agree** — If multiple bases declare the same component with different attributes (e.g. one marks it `[Interpolated]` and another marks it `[VariableUpdateOnly]`), this is an error.
- **Default values must match** — If multiple bases provide default values for the same component, the values must be identical. Providing different defaults is an error.
- **One default is enough** — If only one base provides a default and others don't, the default is used. The component becomes optional at the `AddEntity` call site.

```csharp
// Both bases declare Position — this is fine as long as
// attributes and defaults are compatible
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

Templates can declare multiple **partitions** — mutually exclusive tag combinations that define which sections of memory the entity belongs to. Entities in different partitions are stored in separate contiguous arrays, enabling efficient partition transitions and targeted iteration.

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

Each `IHasPartition` declares a valid partition. The entity always has the base tags (`BallTags.Ball`) plus one of the partition tag sets.

### Partition Transitions

Move entities between partitions with `MoveTo`. In aspects, use the generated extension method:

```csharp
// Ball hits the ground → transition to Resting
ball.MoveTo<BallTags.Ball, BallTags.Resting>(World);

// Rest timer expires → transition to Active
ball.MoveTo<BallTags.Ball, BallTags.Active>(World);
```

Or call `MoveTo` directly on the `WorldAccessor` with an `EntityIndex`:

```csharp
World.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

Systems can target specific partitions:

```csharp
// Only processes Active balls
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void UpdateActive(in ActiveBall ball)
{
    ball.Velocity += Gravity * World.DeltaTime;
}

// Only processes Resting balls
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Resting))]
void UpdateResting(in RestingBall ball)
{
    ball.RestTimer -= World.DeltaTime;
}
```

## Entity Creation

Creating an entity from a template:

```csharp
World.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42));
```

The tag type arguments select the template (and therefore the required components), matched against the templates registered via `WorldBuilder.AddTemplate`.

See [Entities](entities.md) for the full creation API.

## Global Entity Template

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

The global entity is created automatically during world initialization — there is no `AddEntity` call or `EntityInitializer` to set values. Because of this, **all fields in a global template must have explicit default values**.
