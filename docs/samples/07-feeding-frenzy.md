# 07 — Feeding Frenzy

A multi-system simulation with job-based processing. Fish hunt meals, grow when they eat, and shrink from starvation.

**Source:** `com.trecs.core/Samples~/Tutorials/07_FeedingFrenzy/`

## What it does

Fish swim toward meals. On contact a fish consumes the meal, grows, and looks for the next one. Fish shrink from starvation — too small and they die. Up/down arrows adjust population.

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

### Templates with partitions

```csharp
public partial class FishEntity : ITemplate,
    IExtends<CommonTemplates.Renderable>,
    ITagged<FrenzyTags.Fish>,
    IPartitionedBy<FrenzyTags.NotEating, FrenzyTags.Eating>
{
    Velocity Velocity;
    Speed Speed;
    SimPosition SimPosition;
    SimRotation SimRotation;
    TargetMeal TargetMeal;
    DestinationPosition DestinationPosition;
}
```

Fish have two partitions: **NotEating** (idle, bobbing) and **Eating** (moving toward a meal).

## Key systems

### LookingForMealSystem

Pairs idle fish with available meals via nested aspect queries. Sets velocity toward the target and moves both fish and meal into the Eating partition.

### ConsumingMealSystem (`[WrapAsJob]`)

Checks whether eating fish have reached their meal. On contact: removes the meal, grows the fish, moves the fish back to NotEating.

Shows accessing a *different* group inside a `[WrapAsJob]` method — `[FromWorld]` on `mealFactory` lets the job look up meal data by `EntityHandle`:

```csharp
[ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating))]
[WrapAsJob]
static void Execute(
    in ConsumingFish fish,
    in NativeWorldAccessor world,
    [FromWorld(typeof(FrenzyTags.Meal))]
        in MealNutritionView.NativeFactory mealFactory
)
{
    if (math.lengthsq(fish.DestinationPosition - fish.SimPosition) >= EatDistanceSqr)
        return;

    var meal = mealFactory.Create(fish.TargetMeal.ToIndex(world));
    fish.UniformScale = fish.UniformScale + 0.05f * meal.MealNutrition;

    meal.Remove(world);
    fish.TargetMeal = EntityHandle.Null;
    fish.SetTag<FrenzyTags.NotEating>(world);
}
```

### MovementSystem (`[WrapAsJob]`)

Moves eating fish toward their destination position.

### IdleBobSystem (`[WrapAsJob]`)

Applies sinusoidal bobbing to idle fish. `EntityIndex` provides a phase offset so fish bob out of sync:

```csharp
[ForEachEntity(typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating))]
[WrapAsJob]
static void Execute(in Fish fish, EntityIndex entityIndex, in NativeWorldAccessor world)
{
    float phaseOffset = entityIndex.Index * GoldenRatio;
    float y = 0.3f * fish.UniformScale * math.sin(3f * world.ElapsedTime + phaseOffset);
    var pos = fish.SimPosition;
    pos.y = y;
    fish.SimPosition = pos;
}
```

### StarvationSystem (`[WrapAsJob]`)

Shrinks all fish over time, removes those too small, and colors them by current size.

Shows `[PassThroughArgument]` for passing configuration into a job, and entity removal from a parallel job via `NativeWorldAccessor`:

```csharp
[ForEachEntity(typeof(FrenzyTags.Fish))]
[WrapAsJob]
static void ExecuteImpl(
    ref UniformScale scale,
    ref ColorComponent color,
    EntityIndex entityIndex,
    in NativeWorldAccessor world,
    [PassThroughArgument] Settings settings
)
{
    scale.Value -= settings.ShrinkRate * world.DeltaTime;

    if (scale.Value <= settings.MinScale)
    {
        world.RemoveEntity(entityIndex);
        return;
    }

    // Color indicates starvation: cyan (healthy) → red-orange (starving)
    float healthRaw = (scale.Value - settings.MinScale) / (settings.HealthyScale - settings.MinScale);
    float health = math.saturate(healthRaw / settings.HealthyColorThreshold);
    color.Value = Color.Lerp(settings.StarvingColor, settings.HealthyColor, health);
}
```

### VisualSmoothingSystem (`[ExecuteIn(SystemPhase.Presentation)]`)

Each visual frame, lerps `Position`/`Rotation` toward `SimPosition`/`SimRotation`. Movement stays smooth at the display rate even though the simulation runs at a lower fixed timestep:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class VisualSmoothingSystem : ISystem
{
    [ForEachEntity(typeof(FrenzyTags.Fish))]
    [WrapAsJob]
    static void Execute(in Fish fish, in NativeWorldAccessor world)
    {
        float t = math.saturate(world.DeltaTime * ChaseSpeed);
        fish.Position = math.lerp(fish.Position, fish.SimPosition, t);
        fish.Rotation = math.slerp(fish.Rotation, fish.SimRotation, t);
    }

    partial struct Fish : IAspect, IRead<SimPosition, SimRotation>, IWrite<Position, Rotation> { }
}
```

### RemoveCleanupHandler

Bidirectional cleanup — removing a fish also removes its target meal, and vice versa, preventing orphans:

```csharp
public partial class RemoveCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public RemoveCleanupHandler(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

        World.Events.EntitiesWithTags<FrenzyTags.Fish>()
            .OnRemoved(OnFishRemoved)
            .AddTo(_disposables);

        World.Events.EntitiesWithTags<FrenzyTags.Meal>()
            .OnRemoved(OnMealRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnFishRemoved(in TargetMeal targetMeal)
    {
        if (targetMeal.Value.Exists(World))
            World.RemoveEntity(targetMeal.Value);
    }

    [ForEachEntity]
    void OnMealRemoved(in ApproachingFish fish)
    {
        if (fish.Value.Exists(World))
            World.RemoveEntity(fish.Value);
    }

    public void Dispose() => _disposables.Dispose();
}
```

## Architecture pattern: SimPosition vs Position

The simulation writes to `SimPosition` (the "true" position at fixed rate). A variable-update system lerps `Position` toward `SimPosition` each display frame:

```
Fixed Update:  SimPosition jumps to new position
Variable Update:  Position = lerp(Position, SimPosition, smoothFactor)
Rendering:  Reads Position for smooth visual movement
```

An alternative to the formal [interpolation](09-interpolation.md) system. The longer interpolation interval lets fish rotate smoothly to new directions.

## Concepts introduced

- **Native aspect factories** — `MealNutritionView.NativeFactory` lets a job look up another entity's components by handle inside Burst. See [Advanced Job Features](../advanced/advanced-jobs.md) and [Aspects](../data-access/aspects.md).
- **Multi-system simulation** with many interacting systems.
- **Entity population management** — dynamically adjusting fish/meal counts via `[WrapAsJob]` and `MaxFishChangePerFrame` throttling.
- **Bidirectional references** with cleanup handlers — see [Entity Events](../entity-management/entity-events.md).
- **Partition transitions** between NotEating and Eating — see [Partitions](06-partitions.md).
- **Generic tags** — `NotEating` and `Eating` represent dynamic states reused across templates.
- **Visual smoothing** — separating simulation position (`SimPosition`, fixed) from render position (`Position`, variable). For the formal alternative, see [Interpolation](../advanced/interpolation.md) and [sample 09](09-interpolation.md).
- **`[VariableUpdateOnly]`** components — see [Accessor Roles](../advanced/accessor-roles.md).
