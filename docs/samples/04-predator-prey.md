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

Plus `Speed` and `MoveDirection` from this sample, and `Position` from `Common/`.

### Tags & templates

```csharp
public struct Predator : ITag { }
public struct Prey : ITag { }
public struct Movable : ITag { }
```

Template inheritance shares common movement components. `Movable` is abstract — it exists only as an `IExtends` base, so registering it directly would trip `TRECS039`. Concrete templates extend it and set their own `PrefabId`:

```csharp
public abstract partial class Movable
    : ITemplate,
        IExtends<CommonTemplates.RenderableGameObject>,
        ITagged<SampleTags.Movable>
{
    Position Position = default;
    MoveDirection MoveDirection = default;
    Speed Speed;
}

public partial class PredatorEntity
    : ITemplate,
        ITagged<SampleTags.Predator>,
        IExtends<Movable>
{
    ChosenPrey ChosenPrey = default;
    PrefabId PrefabId = new(PredatorPreyPrefabs.Predator);
}

public partial class PreyEntity
    : ITemplate,
        ITagged<SampleTags.Prey>,
        IExtends<Movable>
{
    ApproachingPredator ApproachingPredator = default;
    PrefabId PrefabId = new(PredatorPreyPrefabs.Prey);
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

### GameObject cleanup

When prey are removed, the shared `RenderableGameObjectManager` (from `Common/`) automatically tears down the companion GameObject. It subscribes once to `OnAdded` / `OnRemoved` for every entity carrying `PrefabId` + `GameObjectId`, so concrete samples don't write their own cleanup observer — see [Sample 16 — Reactive Events](16-reactive-events.md) for the same `OnRemoved` pattern in user code, and [Entity Events](../entity-management/entity-events.md) for the API. Events are preferred over inline destruction in the lifetime system because:

- **Consistency** — entity removal is deferred, so the entity still exists until submission. Without an event, later systems could read stale data on an about-to-be-removed entity.
- **Centralized cleanup** — if entities can be removed from multiple places (caught, starved, despawned), the same handler runs regardless of source.

## Concepts introduced

- **`EntityHandle`** — stable cross-entity references that survive structural changes. See [Entities](../core/entities.md).
- **Template inheritance** via `IExtends<T>` to share common components. See [Templates](../core/templates.md).
- **Nested aspect queries** — iterate one template while querying another. See [Queries & Iteration](../data-access/queries-and-iteration.md).
- **Entity events** (`OnRemoved`) for cross-reference cleanup. See [Entity Events](../entity-management/entity-events.md).
- **Bidirectional linking** — predator points to prey and prey points back.
