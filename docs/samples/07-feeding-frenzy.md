# 07 — Feeding Frenzy

A complex simulation with multiple interacting systems and job-based processing. Fish hunt meals, grow when they eat, and shrink from starvation.

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

Checks if eating fish have reached their meal. On contact: removes the meal, grows the fish, transitions the fish back to the NotEating partition.

Demonstrates accessing entities from a *different* group inside a `[WrapAsJob]` method — the `[FromWorld]` attribute on the `mealFactory` parameter lets the job look up meal data by `EntityHandle`:

```csharp
[ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.Eating) })]
[WrapAsJob]
static void Execute(
    in ConsumingFish fish,
    in NativeWorldAccessor world,
    [FromWorld(Tag = typeof(FrenzyTags.Meal))]
        in MealNutritionView.NativeFactory mealFactory
)
{
    if (math.lengthsq(fish.DestinationPosition - fish.SimPosition) >= EatDistanceSqr)
        return;

    var meal = mealFactory.Create(fish.TargetMeal.ToIndex(world));
    fish.UniformScale = fish.UniformScale + 0.05f * meal.MealNutrition;

    meal.Remove(world);
    fish.TargetMeal = EntityHandle.Null;
    fish.MoveTo<FrenzyTags.Fish, FrenzyTags.NotEating>(world);
}
```

### MovementSystem (`[WrapAsJob]`)

Moves eating fish toward their destination position.

### IdleBobSystem (`[WrapAsJob]`)

Applies sinusoidal bobbing to idle (NotEating) fish. Uses `EntityIndex` as a phase offset so fish bob at different times:

```csharp
[ForEachEntity(Tags = new[] { typeof(FrenzyTags.Fish), typeof(FrenzyTags.NotEating) })]
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

Shrinks all fish over time. Removes fish that are too small. Colors fish based on their current size.

Demonstrates `[PassThroughArgument]` to pass configuration into a job, and entity removal inside a parallel job via `NativeWorldAccessor`:

```csharp
[ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
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

### VisualSmoothingSystem (`[Phase(SystemPhase.Presentation)]`)

Lerps `Position`/`Rotation` toward `SimPosition`/`SimRotation` each visual frame. This creates smooth movement at the display frame rate even though the simulation runs at a lower fixed timestep:

```csharp
[Phase(SystemPhase.Presentation)]
public partial class VisualSmoothingSystem : ISystem
{
    [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
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

Bidirectional cleanup — when a fish is removed, its target meal is also removed (and vice versa), preventing orphaned entities:

```csharp
public partial class RemoveCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public RemoveCleanupHandler(World world)
    {
        World = world.CreateAccessor();

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

## Architecture Pattern: SimPosition vs Position

The simulation writes to `SimPosition` (the "true" position at fixed rate). A variable-update system smoothly interpolates `Position` toward `SimPosition` at the display frame rate:

```
Fixed Update:  SimPosition jumps to new position
Variable Update:  Position = lerp(Position, SimPosition, smoothFactor)
Rendering:  Reads Position for smooth visual movement
```

This is an alternative to the formal [interpolation](09-interpolation.md) system.  This approach is nice because it interpolates over longer time intervals so fish rotate smoothly to new directions.

## Concepts Introduced

- **Native Aspect Factories** for data bundling within jobs via a dynamic entity handle
- **Complex multi-system simulation** with many interacting systems
- **Entity population management** — dynamically adjusting fish/meal counts
- **Bidirectional entity references** with cleanup handlers
- **Partition transitions** between NotEating and Eating
- **Generic Tags** NotEating and Eating tags represent dynamic states unrelated to specific entity types
- **Visual smoothing** — separating simulation position from render position
