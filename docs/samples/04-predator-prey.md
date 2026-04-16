# 04 — Predator Prey

Cross-entity relationships using `EntityHandle`. Predators chase prey, and cleanup handlers prevent dangling references.

**Source:** `Samples/04_PredatorPrey/`

## What It Does

Predators (red) chase the nearest prey (green). When a predator catches its prey, the prey is removed and a new one spawns. Predators continuously re-target the nearest available prey.

## Schema

### Components

```csharp
public struct ChosenPrey : IEntityComponent
{
    public EntityHandle Value;  // Handle to the prey being chased
}

public struct ApproachingPredator : IEntityComponent
{
    public EntityHandle Value;  // Handle to the predator chasing this prey
}
```

Plus `Position`, `Velocity`, `Speed`, `MoveDirection`, `GameObjectId`.

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
    public Position Position = Position.Default;
    public MoveDirection MoveDirection;
    public Speed Speed;
    public GameObjectId GameObjectId;
}

// Predator extends Movable
public partial class PredatorEntity : ITemplate,
    IExtends<Movable>,
    IHasTags<SampleTags.Predator>
{
    public ChosenPrey ChosenPrey;
}

// Prey extends Movable
public partial class PreyEntity : ITemplate,
    IExtends<Movable>,
    IHasTags<SampleTags.Prey>
{
    public ApproachingPredator ApproachingPredator;
}
```

## Systems

### PredatorChoosePreySystem

Finds the nearest unassigned prey for each predator using nested aspect queries:

```csharp
foreach (var predator in PredatorView.Query(World).WithTags<SampleTags.Predator>())
{
    if (!predator.ChosenPrey.IsNull) continue;  // Already has a target

    float bestDist = float.MaxValue;
    EntityIndex bestPrey = EntityIndex.Null;

    foreach (var prey in PreyView.Query(World).WithTags<SampleTags.Prey>())
    {
        if (!prey.ApproachingPredator.IsNull) continue;  // Already claimed

        float dist = math.distance(predator.Position, prey.Position);
        if (dist < bestDist)
        {
            bestDist = dist;
            bestPrey = prey.EntityIndex;
        }
    }

    if (!bestPrey.IsNull)
    {
        // Link predator ↔ prey
        predator.ChosenPrey = bestPrey.ToHandle(World);
        World.Component<ApproachingPredator>(bestPrey).Write =
            new ApproachingPredator { Value = predator.EntityIndex.ToHandle(World) };
    }
}
```

### PredatorChaseSystem

Steers predators toward their target prey, removes prey on contact.

### PreyRespawnSystem

Maintains the prey population by spawning replacements.

### Cleanup Handler

When prey are removed, clear the predator's `ChosenPrey` reference to prevent dangling handles:

```csharp
World.Events.InGroupsWithTags<SampleTags.Prey>()
    .OnRemoved((group, range, world) =>
    {
        for (int i = range.Start; i < range.End; i++)
        {
            var preyIndex = new EntityIndex(i, group);
            var approachingPredator = world.Component<ApproachingPredator>(preyIndex).Read;

            if (!approachingPredator.Value.IsNull &&
                approachingPredator.Value.TryToIndex(world, out var predatorIndex))
            {
                world.Component<ChosenPrey>(predatorIndex).Write = default;
            }
        }
    });
```

## Concepts Introduced

- **`EntityHandle`** for stable cross-entity references that survive structural changes
- **Template inheritance** with `IExtends<T>` to share common components
- **Nested aspect queries** — iterating one entity type while querying another
- **Entity events** (`OnRemoved`) for cleanup of cross-references
- **Bidirectional linking** — predator points to prey and prey points back to predator
