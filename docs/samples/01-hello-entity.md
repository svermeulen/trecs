# 01 — Hello Entity

The simplest Trecs sample — a spinning cube. Introduces the fundamental building blocks: components, tags, templates, systems, and world setup.

**Source:** `Samples/01_HelloEntity/`

## What It Does

A cube rotates continuously around the Y axis. The rotation speed is configured when the system is created.

## Schema

### Components

```csharp
[Unwrap]
public partial struct Rotation : IEntityComponent
{
    public quaternion Value;
}
```

The `GameObjectId` component (from Common) maps the entity to a Unity GameObject.

### Tags

```csharp
public struct Spinner : ITag { }
```

### Template

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<SampleTags.Spinner>
{
    public Rotation Rotation = new(quaternion.identity);
    public GameObjectId GameObjectId;
}
```

## Systems

### SpinnerSystem (Fixed Update)

Rotates all entities that have a `Rotation` component:

```csharp
public partial class SpinnerSystem : ISystem
{
    readonly float _rotationSpeed;

    public SpinnerSystem(float rotationSpeed)
    {
        _rotationSpeed = rotationSpeed;
    }

    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Rotation rotation)
    {
        float angle = World.FixedDeltaTime * _rotationSpeed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

Uses `MatchByComponents = true` to iterate all entities with `Rotation`, regardless of tags.

### SpinnerGameObjectUpdater (Variable Update)

Syncs the ECS rotation to the Unity transform:

```csharp
[VariableUpdate]
public partial class SpinnerGameObjectUpdater : ISystem
{
    readonly GameObjectRegistry _gameObjectRegistry;

    public SpinnerGameObjectUpdater(GameObjectRegistry gameObjectRegistry)
    {
        _gameObjectRegistry = gameObjectRegistry;
    }

    [ForEachEntity(MatchByComponents = true)]
    void Execute(in GameObjectId id, in Rotation rotation)
    {
        var go = _gameObjectRegistry.Resolve(id);
        go.transform.rotation = rotation.Value;
    }
}
```

Marked `[VariableUpdate]` because it touches Unity GameObjects — rendering should happen at the display frame rate, not the fixed timestep.

## World Setup

```csharp
var world = new WorldBuilder()
    .AddTemplate(SampleTemplates.SpinnerEntity.Template)
    .AddSystem(new SpinnerSystem(rotationSpeed: 2f))
    .AddSystem(new SpinnerGameObjectUpdater(gameObjectRegistry))
    .BuildAndInitialize();

var ecs = world.CreateAccessor();

ecs.AddEntity<SampleTags.Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(gameObjectRegistry.Register(cube.gameObject))
    .AssertComplete();
```

## Concepts Introduced

- **Components** are unmanaged structs implementing `IEntityComponent`
- **Tags** are empty structs implementing `ITag` that classify entities
- **Templates** declare which components and tags an entity has
- **Systems** implement `ISystem` and use `[ForEachEntity]` for iteration
- **`[VariableUpdate]`** separates rendering from simulation
- **`MatchByComponents`** iterates by component presence instead of tags
- Constructor injection passes dependencies to systems
