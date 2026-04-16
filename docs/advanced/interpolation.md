# Interpolation

Trecs runs simulation at a fixed timestep (default 1/60s), but rendering happens at the variable frame rate. Interpolation smooths component values between fixed frames to prevent visual stuttering.

## How It Works

1. **Before fixed update:** The previous component value is saved (`InterpolatedPrevious<T>`)
2. **During fixed update:** Systems update the component normally
3. **During variable update:** The interpolated value is computed by blending previous → current based on how far through the fixed frame we are (`Interpolated<T>`)
4. **Rendering:** Read from `Interpolated<T>` for smooth visuals

## Setup

### 1. Mark Fields as Interpolated

In your template, use the `[Interpolated]` attribute:

```csharp
public partial class SmoothOrbitEntity : ITemplate, IHasTags<OrbitTags.Smooth>
{
    [Interpolated]
    public Position Position = Position.Default;

    public OrbitParams OrbitParams;
    public GameObjectId GameObjectId;
}
```

This generates three components: `Position`, `Interpolated<Position>`, and `InterpolatedPrevious<Position>`.

### 2. Register the Previous Saver

```csharp
var world = new WorldBuilder()
    .AddEntityType(SampleTemplates.SmoothOrbitEntity.Template)
    .AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<Position>())
    .Build();
```

### 3. Define the Interpolation Function

```csharp
static void InterpolatePosition(
    in Position previous,
    in Position current,
    ref Position result,
    float percentThroughFixedFrame,
    WorldAccessor world)
{
    result.Value = math.lerp(previous.Value, current.Value, percentThroughFixedFrame);
}
```

### 4. Add the Interpolation System

```csharp
world.AddSystem(new InterpolatedUpdater<Position>(InterpolatePosition));
```

### 5. Create Entities with Interpolated Values

```csharp
world.AddEntity<OrbitTags.Smooth>()
    .SetInterpolated(new Position(startPos))  // Sets all three components
    .Set(new OrbitParams { ... });
```

### 6. Read Interpolated Values for Rendering

In your variable-update rendering system, read from `Interpolated<Position>` instead of `Position`:

```csharp
[VariableUpdate]
public partial class RenderSystem : ISystem
{
    [ForEachEntity(Tag = typeof(OrbitTags.Smooth))]
    void Execute(in Interpolated<Position> interpolatedPos, in GameObjectId id)
    {
        var go = _registry.Resolve(id);
        go.transform.position = (Vector3)interpolatedPos.Value.Value;
    }
}
```

## Without Interpolation

For comparison, entities without interpolation update visually only at fixed timestep boundaries, causing visible stuttering at low fixed rates or high frame rates:

```csharp
public partial class RawOrbitEntity : ITemplate, IHasTags<OrbitTags.Raw>
{
    public Position Position = Position.Default;  // No [Interpolated]
    public OrbitParams OrbitParams;
    public GameObjectId GameObjectId;
}
```

## Custom Interpolation

The interpolation function can implement any blending logic — linear, spherical, or custom curves:

```csharp
// Quaternion interpolation
static void InterpolateRotation(
    in Rotation previous,
    in Rotation current,
    ref Rotation result,
    float t,
    WorldAccessor world)
{
    result.Value = math.slerp(previous.Value, current.Value, t);
}
```
