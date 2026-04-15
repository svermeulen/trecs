# Templates

Templates define the component layout and tag identity of an entity — like a blueprint. Source generation creates the builder code that makes `AddEntity` work with type safety and validation.

## Defining a Template

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<SampleTags.Spinner>
{
    public Rotation Rotation = new(quaternion.identity);  // Default value
    public GameObjectId GameObjectId;                      // No default (must be set)
}
```

A template is a `partial class` that:

1. Implements **`ITemplate`**
2. Declares **tags** via `IHasTags<T1, ...>`
3. Declares **component fields** (the entity's data layout)

Fields with default values provide fallback initialization. Fields without defaults must be set via `EntityInitializer.Set()`.

## Tags

Every template declares one or more tags via `IHasTags`:

```csharp
// Single tag
public partial class BulletEntity : ITemplate, IHasTags<GameTags.Bullet>
{
    public Position Position;
    public Velocity Velocity;
}

// Multiple tags
public partial class PlayerBullet : ITemplate, IHasTags<GameTags.Bullet, GameTags.Player>
{
    public Position Position;
    public Velocity Velocity;
    public Damage Damage;
}
```

See [Tags & Groups](tags-and-groups.md) for details on how tags define entity grouping.

## Template Inheritance

Use `IExtends<T>` to inherit components from a base template:

```csharp
// Base template
public partial class Renderable : ITemplate, IHasTags<CommonTags.Renderable>
{
    public Position Position;
    public Rotation Rotation;
    public UniformScale Scale;
    public ColorComponent Color = new(Color.white);
}

// Extended template — inherits Position, Rotation, Scale, Color
public partial class FishEntity : ITemplate,
    IExtends<Renderable>,
    IHasTags<FrenzyTags.Fish>
{
    public Velocity Velocity;
    public Speed Speed;
}
```

Multiple inheritance is supported (up to 4 base templates):

```csharp
public partial class ComplexEntity : ITemplate,
    IExtends<Renderable, Moveable>,
    IHasTags<GameTags.Complex>
{
    public Health Health;
}
```

## States

Templates can declare multiple **states** — mutually exclusive tag combinations that define which group the entity belongs to. This enables efficient state machines where entities in different states are stored in separate contiguous arrays.

```csharp
public partial class BallEntity : ITemplate,
    IHasTags<BallTags.Ball>,
    IHasState<BallTags.Active>,
    IHasState<BallTags.Resting>
{
    public Position Position;
    public Velocity Velocity;
    public RestTimer RestTimer;
    public GameObjectId GameObjectId;
}
```

Each `IHasState` declares a valid state. The entity always has the base tags (`BallTags.Ball`) plus exactly one state tag.

### State Transitions

Move entities between states with `MoveTo`:

```csharp
// Ball hits the ground → transition to Resting
World.MoveTo<BallTags.Ball, BallTags.Resting>(ball.EntityIndex);

// Rest timer expires → transition to Active
World.MoveTo<BallTags.Ball, BallTags.Active>(ball.EntityIndex);
```

Systems can target specific states:

```csharp
// Only processes Active balls
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void UpdateActive(in ActiveBall ball)
{
    ball.Velocity += Gravity * World.DeltaTime;
}

// Only processes Resting balls
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
void UpdateResting(in RestingBall ball)
{
    ball.RestTimer -= World.DeltaTime;
}
```

## Entity Creation

Creating an entity from a template:

```csharp
ecs.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(new GameObjectId(42))
    .AssertComplete();
```

The tag type arguments determine which template (and therefore which components) are required. `AssertComplete()` verifies that all required components have been set.

## Global Entity Template

Extend the framework's global template to add world-wide components:

```csharp
public partial class MyGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    public Score Score;
    public DesiredFishCount DesiredFishCount = new() { Value = 100 };

    [Input(MissingInputFrameBehaviour.RetainCurrent)]
    public MoveInput MoveInput;
}
```
