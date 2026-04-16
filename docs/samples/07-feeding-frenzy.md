# 07 — Feeding Frenzy

A complex simulation with multiple interacting systems, job-based processing, and visual smoothing. Fish hunt meals, grow when they eat, and shrink from starvation.

**Source:** `Samples/07_FeedingFrenzy/`

## What It Does

Fish swim toward meals. When a fish reaches a meal, it consumes it, grows slightly, and looks for the next one. Fish slowly shrink from starvation — if they get too small, they die. Up/down arrows adjust the fish population.

## Schema

### Components

```csharp
public struct SimPosition : IEntityComponent { public float3 Value; }
public struct SimRotation : IEntityComponent { public quaternion Value; }
public struct TargetMeal : IEntityComponent { public EntityHandle Value; }
public struct ApproachingFish : IEntityComponent { public EntityHandle Value; }
public struct DestinationPosition : IEntityComponent { public float3 Value; }
public struct MealNutrition : IEntityComponent { public float Value; }
```

Plus `Position`, `Rotation`, `Velocity`, `Speed`, `UniformScale`, `ColorComponent` from Common.

### Templates with Partitions

```csharp
public partial class FishEntity : ITemplate,
    IExtends<CommonTemplates.Renderable>,
    IHasTags<FrenzyTags.Fish>,
    IHasPartition<FrenzyTags.NotEating>,
    IHasPartition<FrenzyTags.Eating>
{
    public Velocity Velocity;
    public Speed Speed;
    public SimPosition SimPosition;
    public SimRotation SimRotation;
    public TargetMeal TargetMeal;
    public DestinationPosition DestinationPosition;
}
```

Fish have two partitions: **NotEating** (idle, bobbing) and **Eating** (moving toward a meal).

## Key Systems

### LookingForMealSystem

Pairs idle fish with available meals using nested aspect queries. Sets velocity toward the target and transitions both fish and meal to the Eating partition.

### ConsumingMealSystem (`[WrapAsJob]`)

Burst-compiled parallel job. Checks if eating fish have reached their meal. On contact: removes the meal, grows the fish, transitions the fish back to the NotEating partition.

### MovementSystem (`[WrapAsJob]`)

Moves eating fish toward their destination position.

### IdleBobSystem (`[WrapAsJob]`)

Applies sinusoidal bobbing to idle (NotEating) fish. Uses `EntityIndex` as a phase offset so fish bob at different times.

### StarvationSystem (`[WrapAsJob]`)

Shrinks all fish over time. Removes fish that are too small. Colors fish based on their current size (health indicator).

### VisualSmoothingSystem (`[VariableUpdate]`)

Lerps `Position`/`Rotation` toward `SimPosition`/`SimRotation` each visual frame. This creates smooth movement at the display frame rate even though the simulation runs at a lower fixed timestep.

### RemoveCleanupHandler

When a fish is removed, also handles its target meal — clears the `ApproachingFish` reference to prevent dangling handles.

## Architecture Pattern: SimPosition vs Position

The simulation writes to `SimPosition` (the "true" position at fixed rate). A variable-update system smoothly interpolates `Position` toward `SimPosition` at the display frame rate:

```
Fixed Update:  SimPosition jumps to new position
Variable Update:  Position = lerp(Position, SimPosition, smoothFactor)
Rendering:  Reads Position for smooth visual movement
```

This is an alternative to the formal [interpolation](09-interpolation.md) system — simpler to set up but less mathematically precise.

## Concepts Introduced

- **Complex multi-system simulation** with many interacting systems
- **`[WrapAsJob]`** for Burst-compiled parallel processing
- **Visual smoothing** — separating simulation position from render position
- **Entity population management** — dynamically adjusting fish/meal counts
- **Bidirectional entity references** with cleanup handlers
- **Partition transitions** between NotEating and Eating
