# 09 — Interpolation

Smooth rendering at variable frame rates despite a low fixed timestep. Side-by-side comparison of interpolated vs raw entity movement.

**Source:** `Samples/09_Interpolation/`

## What It Does

Two sets of entities orbit in circles. "Smooth" entities use interpolation and appear silky smooth. "Raw" entities read the fixed-update position directly and visibly stutter at high frame rates.

The fixed timestep is intentionally set low (10 Hz) to make the difference obvious.

## Schema

### Components

```csharp
public struct OrbitParams : IEntityComponent
{
    public float Radius;
    public float Speed;
    public float Phase;
    public float CenterX;
}
```

### Templates

```csharp
// Interpolated — smooth
public partial class SmoothOrbitEntity : ITemplate, IHasTags<OrbitTags.Smooth>
{
    [Interpolated]
    public Position Position = Position.Default;
    public OrbitParams OrbitParams;
    public GameObjectId GameObjectId;
}

// Not interpolated — jittery
public partial class RawOrbitEntity : ITemplate, IHasTags<OrbitTags.Raw>
{
    public Position Position = Position.Default;
    public OrbitParams OrbitParams;
    public GameObjectId GameObjectId;
}
```

The `[Interpolated]` attribute on `Position` automatically generates `Interpolated<Position>` and `InterpolatedPrevious<Position>` components.

## Setup

```csharp
var world = new WorldBuilder()
    .AddEntityType(SampleTemplates.SmoothOrbitEntity.Template)
    .AddEntityType(SampleTemplates.RawOrbitEntity.Template)
    .AddInterpolatedPreviousSaver(new InterpolatedPreviousSaver<Position>())
    .Build();

world.AddSystem(new InterpolatedUpdater<Position>(InterpolatePosition));
```

The interpolation function:

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

### Entity Creation

Smooth entities use `SetInterpolated` to initialize all three components at once:

```csharp
world.AddEntity<OrbitTags.Smooth>()
    .SetInterpolated(new Position(startPos))
    .Set(new OrbitParams { ... });
```

## Rendering

The renderer reads `Interpolated<Position>` for smooth entities and `Position` for raw entities:

```csharp
// Smooth: reads blended position
[ForEachEntity(Tag = typeof(OrbitTags.Smooth))]
void RenderSmooth(in Interpolated<Position> pos, in GameObjectId id) { ... }

// Raw: reads fixed-update position directly
[ForEachEntity(Tag = typeof(OrbitTags.Raw))]
void RenderRaw(in Position pos, in GameObjectId id) { ... }
```

## Concepts Introduced

- **`[Interpolated]`** attribute generates wrapper components
- **`InterpolatedPreviousSaver<T>`** — saves previous frame value before fixed update
- **`InterpolatedUpdater<T>`** — blends previous and current at variable frame rate
- **`SetInterpolated()`** — initializes all three components (current, interpolated, previous)
- **Custom interpolation functions** — implement any blending logic (lerp, slerp, etc.)
