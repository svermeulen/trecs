# 09 — Interpolation

Smooth rendering at variable frame rates despite a low fixed timestep. Side-by-side comparison of interpolated vs raw entity movement.

**Source:** `Samples/09_Interpolation/`

## What It Does

Two sets of entities orbit in circles. "Smooth" entities use interpolation and appear silky smooth. "Raw" entities read the fixed-update position directly and visibly stutter at high frame rates.

The fixed timestep is intentionally set low (10 Hz) to make the difference obvious. An alternative approach to see this effect would be to keep default timestep but adjust Unity Time.scale to slow down the simulation. In a real game the latter approach is more useful to debug this effect.

## Schema

### Components

```csharp
public partial struct OrbitParams : IEntityComponent
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
    Position Position = default;

    [Interpolated]
    Rotation Rotation = default;
    OrbitParams OrbitParams;
    GameObjectId GameObjectId;
}

// Not interpolated — jittery
public partial class RawOrbitEntity : ITemplate, IHasTags<OrbitTags.Raw>
{
    Position Position = default;
    Rotation Rotation = default;
    OrbitParams OrbitParams;
    GameObjectId GameObjectId;
}
```

The `[Interpolated]` attribute on `Position` and `Rotation` automatically generates `Interpolated<T>` and `InterpolatedPrevious<T>` wrapper components.

## Interpolation Functions

Define static methods with `[GenerateInterpolatorSystem]` that specify how each component type should be blended. The source generator creates a Burst-compiled job system for each:

```csharp
public static class SampleInterpolators
{
    const string GroupName = "InterpolationSampleInterpolators";

    [GenerateInterpolatorSystem("PositionInterpolatedUpdater", GroupName)]
    [BurstCompile]
    public static void InterpolatePosition(
        in Position a, in Position b, ref Position result, float t)
    {
        result.Value = math.lerp(a.Value, b.Value, t);
    }

    [GenerateInterpolatorSystem("RotationInterpolatedUpdater", GroupName)]
    [BurstCompile]
    public static void InterpolateRotation(
        in Rotation a, in Rotation b, ref Rotation result, float t)
    {
        // nlerp is sufficient — the angular delta between fixed frames
        // is small enough that the difference from slerp is imperceptible
        result.Value = math.nlerp(a.Value, b.Value, t);
    }
}
```

The `GroupName` groups related interpolators so they can be registered with a single call.

## Setup

The generated extension method `AddInterpolationSampleInterpolators()` registers both the previous-frame savers and the blending systems:

```csharp
var world = new WorldBuilder()
    .AddEntityTypes(new[]
    {
        SampleTemplates.SmoothOrbitEntity.Template,
        SampleTemplates.RawOrbitEntity.Template,
    })
    .AddInterpolationSampleInterpolators()  // Generated — registers all interpolators
    .Build();
```

### Entity Creation

Smooth entities use `SetInterpolated` to initialize all three components at once:

```csharp
world.AddEntity<OrbitTags.Smooth>()
    .SetInterpolated(new Position(startPos))
    .SetInterpolated(new Rotation(startRot))
    .Set(new OrbitParams { ... });
```

## Rendering

The renderer reads `Interpolated<Position>` and `Interpolated<Rotation>` for smooth entities, and the raw components directly for comparison:

```csharp
// Smooth: reads blended values — silky smooth
[ForEachEntity(typeof(OrbitTags.Smooth))]
void RenderSmooth(in Interpolated<Position> pos, in Interpolated<Rotation> rot, in GameObjectId id)
{
    var go = _registry.Resolve(id);
    go.transform.SetPositionAndRotation((Vector3)pos.Value.Value, rot.Value.Value);
}

// Raw: reads fixed-update values directly — visibly jittery
[ForEachEntity(typeof(OrbitTags.Raw))]
void RenderRaw(in Position pos, in Rotation rot, in GameObjectId id)
{
    var go = _registry.Resolve(id);
    go.transform.SetPositionAndRotation((Vector3)pos.Value, rot.Value);
}
```

## Concepts Introduced

- **`[Interpolated]`** attribute on template fields generates wrapper components
- **`[GenerateInterpolatorSystem]`** source-generates Burst-compiled blending systems from simple static methods
- **`GroupName`** groups related interpolators for single-call registration
- **`SetInterpolated()`** initializes all three components (current, interpolated, previous)
- **`Interpolated<T>`** — the blended value, read by renderers for smooth visuals

See [Interpolation](../advanced/interpolation.md) for the full reference.
