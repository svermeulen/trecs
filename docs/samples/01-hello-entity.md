# 01 — Hello Entity

A spinning cube. Introduces components, tags, templates, systems, and world setup.

**Source:** `com.trecs.core/Samples~/Tutorials/01_HelloEntity/`

## What it does

A single entity holds a `Rotation` component. A fixed-update system advances the rotation each tick; a presentation system applies it to a Unity `Transform`.

## Schema

A schema is the components, tags, and templates that describe your entities:

```csharp
public static class SampleTags
{
    public struct Spinner : ITag { }
}

public static partial class SampleTemplates
{
    public partial class SpinnerEntity : ITemplate, ITagged<SampleTags.Spinner>
    {
        Rotation Rotation = new(quaternion.identity);
    }
}
```

`Rotation` comes from `Common/` and is reused across tutorials.

See [Components](../core/components.md), [Tags](../core/tags.md), and [Templates](../core/templates.md).

## Systems

### SpinnerSystem (fixed update)

Spins anything with a `Rotation` component:

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
        float angle = World.DeltaTime * _rotationSpeed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

`MatchByComponents = true` iterates every entity with a `Rotation` component, regardless of tags. See [Queries & Iteration](../data-access/queries-and-iteration.md).

### SpinnerGameObjectUpdater (presentation)

Syncs the simulation rotation onto a Unity `Transform`. The sample wires a single transform in by constructor; later samples introduce the `RenderableGameObjectManager` pattern for per-entity GameObjects:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class SpinnerGameObjectUpdater : ISystem
{
    readonly Transform _spinnerCube;

    public SpinnerGameObjectUpdater(Transform spinnerCube) => _spinnerCube = spinnerCube;

    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Rotation rotation)
    {
        _spinnerCube.rotation = rotation.Value;
    }
}
```

`[ExecuteIn(SystemPhase.Presentation)]` runs at the variable (display) frame rate rather than the fixed timestep — the right place for anything touching Unity GameObjects. See [Systems](../core/systems.md) and [Accessor Roles](../advanced/accessor-roles.md).

## Wiring it up

The composition root builds the world, registers the systems, and hands lifecycle callbacks back to `Bootstrap`:

```csharp
var world = new WorldBuilder()
    .AddTemplate(SampleTemplates.SpinnerEntity.Template)
    .Build();

world.AddSystems(new ISystem[]
{
    new SpinnerSystem(RotationSpeed),
    new SpinnerGameObjectUpdater(spinnerCubeTransform),
});
```

A separate scene initializer creates the entity once during the init phase. Init code lives outside any system, so it uses `AccessorRole.Unrestricted`:

```csharp
public void Initialize()
{
    var world = _world.CreateAccessor(AccessorRole.Unrestricted);
    world.AddEntity<SampleTags.Spinner>();
}
```

See [World Setup](../core/world-setup.md) and [Entities](../core/entities.md).

## Concepts introduced

- **Components** — unmanaged structs implementing `IEntityComponent`
- **Tags** — empty structs implementing `ITag`
- **Templates** — declare an entity's components and tags
- **Systems** — `ISystem` + `[ForEachEntity]` for iteration
- **`MatchByComponents`** — iterate by component presence instead of tags
- **`[ExecuteIn(SystemPhase.Presentation)]`** — run at the display frame rate
