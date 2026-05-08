# 04 — Predator Prey

Cross-entity relationships using `EntityHandle`. Predators chase prey, and cleanup handlers prevent dangling references.

**Source:** `com.trecs.core/Samples~/Tutorials/04_PredatorPrey/`

## What It Does

Predators (red) chase the nearest prey (green). When a predator catches its prey, the prey is removed and a new one spawns. Predators continuously re-target the nearest available prey.

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

Plus `Speed` and `MoveDirection` defined in this sample, and `Position` / `GameObjectId` from `Common/`.

### Tags & Templates

```csharp
public struct Predator : ITag { }
public struct Prey : ITag { }
public struct Movable : ITag { }
```

Template inheritance shares common movement components:

```csharp
// Base template
public partial class Movable : ITemplate, IHasTags<SampleTags.Movable>
{
    Position Position = default;
    MoveDirection MoveDirection = default;
    Speed Speed;
    GameObjectId GameObjectId;
}

// Predator extends Movable
public partial class PredatorEntity : ITemplate,
    IHasTags<SampleTags.Predator>,
    IExtends<Movable>
{
    ChosenPrey ChosenPrey = default;
}

// Prey extends Movable
public partial class PreyEntity : ITemplate,
    IHasTags<SampleTags.Prey>,
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
        chosenPrey.ApproachingPredator = predator.EntityIndex.ToHandle(World);
        predator.ChosenPrey = chosenPrey.EntityIndex.ToHandle(World);
    }
}

partial struct Predator : IAspect, IRead<Position>, IWrite<ChosenPrey> { }
partial struct Prey : IAspect, IRead<Position>, IWrite<ApproachingPredator> { }
```

### PredatorChaseSystem

Steers predators toward their target prey, removes prey on contact.

### PreyRespawnSystem

Maintains the prey population by spawning replacements.

### Cleanup Handler

When prey are removed, clean up their GameObjects using an `OnRemoved` event handler. Using events for cleanup is good practice for two reasons:

- **Consistency** — since entity removal is deferred, the entity still exists until the next submission. If not cleaning up via an event, subsequent systems could attempt to use stale data on the about-to-be-removed entity.
- **Centralized cleanup** — if entities can be removed from multiple places (e.g., caught by a predator, starvation, despawning), the same cleanup handler runs regardless of the removal source.

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

## Concepts Introduced

- **`EntityHandle`** for stable cross-entity references that survive structural changes. See [Entities](../core/entities.md).
- **Template inheritance** with `IExtends<T>` to share common components. See [Templates](../core/templates.md).
- **Nested aspect queries** — iterating one template while querying another. See [Queries & Iteration](../data-access/queries-and-iteration.md).
- **Entity events** (`OnRemoved`) for cleanup of cross-references. See [Entity Events](../entity-management/entity-events.md).
- **Bidirectional linking** — predator points to prey and prey points back to predator.
