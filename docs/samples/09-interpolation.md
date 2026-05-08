# 09 — Interpolation

Smooth rendering at variable frame rates despite a low fixed timestep. Side-by-side comparison of interpolated vs raw entity movement.

**Source:** `com.trecs.core/Samples~/Tutorials/09_Interpolation/`

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
    .AddTemplates(new[]
    {
        SampleTemplates.SmoothOrbitEntity.Template,
        SampleTemplates.RawOrbitEntity.Template,
    })
    .AddInterpolationSampleInterpolators()  // Generated — registers all interpolators
    .Build();
```

### Entity Creation

Smooth entities use `SetInterpolated`, which initializes the current, previous, and interpolated copies of the component in one call:

```csharp
entity.SetInterpolated(new Position(position));
entity.SetInterpolated(new Rotation(quaternion.identity));
```

Raw entities use plain `Set` — they only have the single fixed-update component.

## Rendering

The renderer uses two aspects — one that reads the interpolated wrappers and one that reads the raw components — and dispatches per partition:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class OrbitRendererSystem : ISystem
{
    public void Execute()
    {
        RenderSmooth();
        RenderRaw();
    }

    [ForEachEntity(typeof(OrbitTags.Smooth))]
    void RenderSmooth(in SmoothOrbitView view)
    {
        var go = _registry.Resolve(view.GameObjectId);
        go.transform.position = (Vector3)view.InterpolatedPosition;
        go.transform.rotation = view.InterpolatedRotation;
    }

    [ForEachEntity(typeof(OrbitTags.Raw))]
    void RenderRaw(in RawOrbitView view)
    {
        var go = _registry.Resolve(view.GameObjectId);
        go.transform.position = (Vector3)view.Position;
        go.transform.rotation = view.Rotation;
    }

    partial struct SmoothOrbitView
        : IAspect, IRead<Interpolated<Position>, Interpolated<Rotation>, GameObjectId> { }

    partial struct RawOrbitView : IAspect, IRead<Position, Rotation, GameObjectId> { }
}
```

Because `Position` and `Rotation` are `[Unwrap]`, the aspect exposes `view.InterpolatedPosition` (`float3`) and `view.InterpolatedRotation` (`quaternion`) directly — no double-`.Value` indirection.

## Concepts Introduced

- **`[Interpolated]`** attribute on template fields generates the `Interpolated<T>` and `InterpolatedPrevious<T>` wrapper components.
- **`[GenerateInterpolatorSystem]`** source-generates Burst-compiled blending systems from simple static methods.
- **`GroupName`** groups related interpolators so a single generated extension method (`AddInterpolationSampleInterpolators()`) registers them all.
- **`SetInterpolated()`** initializes all three component copies (current, interpolated, previous).
- **Reading via an aspect** — `IRead<Interpolated<Position>>` together with `[Unwrap]` gives clean `view.InterpolatedPosition` access.

See [Interpolation](../advanced/interpolation.md) for the full reference. For an alternative pattern using a manual `SimPosition` + lerp, see [Feeding Frenzy](07-feeding-frenzy.md).
