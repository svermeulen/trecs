# 04 — Predator Prey

Cross-entity references via `EntityHandle`, with cleanup handlers to prevent dangling references.

**Source:** `com.trecs.core/Samples~/Tutorials/04_PredatorPrey/`

## What it does

Predators (red) chase the nearest prey (green). On contact, the prey is removed and a new one spawns. Predators re-target the nearest available prey.

## Schema

### Components

```csharp
[Unwrap]
public partial struct ChosenPrey : IEntityComponent
{
    public EntityHandle Value;  // Handle to the prey being chased
}

[Unwrap]
public partial struct ApproachingPredator : IEntityComponent
{
    public EntityHandle Value;  // Handle to the predator chasing this prey
}
```

Plus `Speed` and `MoveDirection` from this sample, and `Position` / `GameObjectId` from `Common/`.

### Tags & templates

```csharp
public struct Predator : ITag { }
public struct Prey : ITag { }
public struct Movable : ITag { }
```

Template inheritance shares common movement components:

```csharp
// Base template
public partial class Movable : ITemplate, ITagged<SampleTags.Movable>
{
    Position Position = default;
    MoveDirection MoveDirection = default;
    Speed Speed;
    GameObjectId GameObjectId;
}

// Predator extends Movable
public partial class PredatorEntity : ITemplate,
    ITagged<SampleTags.Predator>,
    IExtends<Movable>
{
    ChosenPrey ChosenPrey = default;
}

// Prey extends Movable
public partial class PreyEntity : ITemplate,
    ITagged<SampleTags.Prey>,
    IExtends<Movable>
{
    ApproachingPredator ApproachingPredator = default;
}
```

## Systems

### PredatorChoosePreySystem

Finds the nearest unassigned prey for each predator using nested aspect queries:

```csharp
foreach (var predator in Predator.Query(World).WithTags<SampleTags.Predator>())
{
    if (!predator.ChosenPrey.IsNull) continue;  // Already has a target

    float nearestDistSq = float.MaxValue;
    Prey chosenPrey = default;
    bool found = false;

    foreach (var prey in Prey.Query(World).WithTags<SampleTags.Prey>())
    {
        if (!prey.ApproachingPredator.IsNull) continue;  // Already claimed

        float distSq = math.distancesq(predator.Position, prey.Position);
        if (distSq < nearestDistSq)
        {
            nearestDistSq = distSq;
            chosenPrey = prey;
            found = true;
        }
    }

    if (found)
    {
        // Link predator ↔ prey via aspects
        chosenPrey.ApproachingPredator = predator.Handle(World);
        predator.ChosenPrey = chosenPrey.Handle(World);
    }
}

partial struct Predator : IAspect, IRead<Position>, IWrite<ChosenPrey> { }
partial struct Prey : IAspect, IRead<Position>, IWrite<ApproachingPredator> { }
```

### PredatorChaseSystem

Steers predators toward their target prey and removes prey on contact.

### PreyRespawnSystem

Maintains the prey population by spawning replacements.

### Cleanup handler

When prey are removed, an `OnRemoved` event handler cleans up GameObjects. Events are preferred for cleanup because:

- **Consistency** — entity removal is deferred, so the entity still exists until submission. Without an event, later systems could read stale data on an about-to-be-removed entity.
- **Centralized cleanup** — if entities can be removed from multiple places (caught, starved, despawned), the same handler runs regardless of source.

```csharp
public partial class CleanupHandlers
{
    readonly GameObjectRegistry _gameObjectRegistry;
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public CleanupHandlers(World world, GameObjectRegistry gameObjectRegistry)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);
        _gameObjectRegistry = gameObjectRegistry;

        World.Events
            .EntitiesWithTags<SampleTags.Prey>()
            .OnRemoved(OnPreyRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnPreyRemoved(in Prey prey)
    {
        var go = _gameObjectRegistry.Resolve(prey.GameObjectId);
        GameObject.Destroy(go);
        _gameObjectRegistry.Unregister(prey.GameObjectId);
    }

    partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
}
```

## Concepts introduced

- **`EntityHandle`** — stable cross-entity references that survive structural changes. See [Entities](../core/entities.md).
- **Template inheritance** via `IExtends<T>` to share common components. See [Templates](../core/templates.md).
- **Nested aspect queries** — iterate one template while querying another. See [Queries & Iteration](../data-access/queries-and-iteration.md).
- **Entity events** (`OnRemoved`) for cross-reference cleanup. See [Entity Events](../entity-management/entity-events.md).
- **Bidirectional linking** — predator points to prey and prey points back.
