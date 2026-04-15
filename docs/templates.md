# Templates

Templates define entity archetypes -- the combination of tags and default component values for a type of entity. They are the primary way to declare what kinds of entities exist in your world.

## Defining Templates

A template is a `partial class` that implements `ITemplate` and one or more tag interfaces:

```csharp
public partial class PlayerEntity : ITemplate, ITags<Player>
{
    public CPosition Position;
    public CVelocity Velocity;
    public CHealth Health = new() { Value = 100 };
}
```

Key points:
- The class must be `partial` for the source generator
- `ITags<T1, T2, ...>` specifies which tags this entity type has (up to 4 type parameters)
- Public fields define the components and their default values
- Fields without explicit initialization use `default`

## Registering Templates

Templates must be registered with the `WorldBuilder`:

```csharp
var world = new WorldBuilder()
    .AddTemplate(MyTemplates.PlayerEntity.Template)
    .AddTemplate(MyTemplates.EnemyEntity.Template)
    .Build();
```

The source generator creates a static `Template` property on each template class.

## Multiple Tags

Entities can have multiple tags:

```csharp
public partial class FlyingEnemy : ITemplate, ITags<Enemy, Flying>
{
    public CPosition Position;
    public CVelocity Velocity;
    public CHealth Health = new() { Value = 50 };
}
```

This entity belongs to the group defined by the combination of `Enemy` + `Flying` tags.

## Organizing Templates

A common pattern is to group templates in a static partial class:

```csharp
public static partial class GameTemplates
{
    public partial class Player : ITemplate, ITags<PlayerTag>
    {
        public CPosition Position;
        public CHealth Health = new() { Value = 100 };
    }

    public partial class Enemy : ITemplate, ITags<EnemyTag>
    {
        public CPosition Position;
        public CHealth Health = new() { Value = 50 };
    }

    public partial class Projectile : ITemplate, ITags<ProjectileTag>
    {
        public CPosition Position;
        public CVelocity Velocity;
        public CDamage Damage;
    }
}
```

## Component Field Attributes

Control how individual component fields behave:

### [FixedUpdateOnly]

Component is only updated during fixed update:

```csharp
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [FixedUpdateOnly]
    public CPhysicsState PhysicsState;
}
```

### [VariableUpdateOnly]

Component is only updated during variable update:

```csharp
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [VariableUpdateOnly]
    public CRenderState RenderState;
}
```

### [Interpolated]

Component values are interpolated between fixed-update steps for smooth rendering:

```csharp
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [Interpolated]
    public CPosition Position;
}
```

See [Advanced Topics](advanced.md) for details on interpolation.

### [Constant]

Component value never changes after initialization:

```csharp
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [Constant]
    public CEntityConfig Config;
}
```

### [Input]

Component receives input data:

```csharp
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [Input]
    public CPlayerInput Input;
}
```

## Creating Entities from Templates

After registering templates and building the world, create entities using the tags:

```csharp
// Create a Player entity with default component values
var entity = accessor.ScheduleAddEntity<PlayerTag>();

// Override specific component values
entity.Set(new CPosition { Value = spawnPoint });
entity.Set(new CHealth { Value = 200 });

// Submit to finalize creation
world.SubmitEntities();
```
