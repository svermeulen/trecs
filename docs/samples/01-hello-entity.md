# 01 — Hello Entity

The simplest Trecs sample — a spinning cube. Introduces the fundamental building blocks: components, tags, templates, systems, and world setup.

**Source:** `com.trecs.core/Samples~/Tutorials/01_HelloEntity/`

## Goal

By the end you'll have a single entity with a `Rotation` component being rotated each fixed update by a system, and its `GameObject` synced to the rotation each rendered frame.

## Schema

A schema is just the components, tags, and templates that describe your entity types. This sample's schema is tiny:

```csharp
public static class SampleTags
{
    public struct Spinner : ITag { }
}

public static partial class SampleTemplates
{
    public partial class SpinnerEntity : ITemplate, IHasTags<SampleTags.Spinner>
    {
        Rotation Rotation = new(quaternion.identity);

        // Components must be unmanaged, so we store an int id that maps
        // to a GameObject via GameObjectRegistry instead of a reference.
        GameObjectId GameObjectId;
    }
}
```

`Rotation` and `GameObjectId` come from `Common/` — the samples folder reuses a small set of basic components across tutorials.

See [Components](../core/components.md), [Tags](../core/tags.md), and [Templates](../core/templates.md) for the underlying concepts.

## Systems

### SpinnerSystem (Fixed Update)

Spins anything that has a `Rotation` component:

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

`MatchByComponents = true` iterates every entity that has the `Rotation` component, regardless of tags. See [Queries & Iteration](../data-access/queries-and-iteration.md).

### SpinnerGameObjectUpdater (Variable Update)

Syncs the simulation rotation onto the Unity transform:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
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

`[ExecuteIn(SystemPhase.Presentation)]` runs this system at the variable (display) frame rate rather than the fixed timestep — appropriate for anything that touches Unity GameObjects. See [Systems](../core/systems.md) and [Accessor Roles](../advanced/accessor-roles.md).

## Wiring it up

The composition root builds the world, registers the systems, and hands callbacks back to `Bootstrap`:

```csharp
var world = new WorldBuilder()
    .AddTemplate(SampleTemplates.SpinnerEntity.Template)
    .Build();

world.AddSystems(new ISystem[]
{
    new SpinnerSystem(RotationSpeed),
    new SpinnerGameObjectUpdater(gameObjectRegistry),
});
```

The single entity is created from a separate `SceneInitializer`, which runs once during the init phase. Init code lives outside any system so it uses `AccessorRole.Unrestricted`:

```csharp
public void Initialize()
{
    var world = _world.CreateAccessor(AccessorRole.Unrestricted);

    var cube = SampleUtil.CreatePrimitive(PrimitiveType.Cube);
    cube.name = "SpinnerCube";

    world
        .AddEntity<SampleTags.Spinner>()
        .Set(_gameObjectRegistry.Register(cube.gameObject));
}
```

See [World Setup](../core/world-setup.md) and [Entities](../core/entities.md).

## Concepts Introduced

- **Components** — unmanaged structs implementing `IEntityComponent`
- **Tags** — empty structs implementing `ITag`
- **Templates** — declare an entity's components and tags
- **Systems** — `ISystem` + `[ForEachEntity]` for iteration
- **`MatchByComponents`** — iterate by component presence instead of tags
- **`[ExecuteIn(SystemPhase.Presentation)]`** — run at the display frame rate
