# 09 — Interpolation

Smooth rendering at variable frame rates despite a low fixed timestep. Side-by-side comparison of interpolated vs raw movement.

**Source:** `com.trecs.core/Samples~/Tutorials/09_Interpolation/`

## What it does

Two sets of entities orbit in circles. "Smooth" entities use interpolation; "Raw" entities read the fixed-update position directly and visibly stutter at high frame rates.

The fixed timestep is set low (10 Hz) to make the difference obvious. An alternative is keeping the default timestep but lowering Unity's `Time.scale` — more useful for debugging in a real game.

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
public partial class SmoothOrbitEntity
    : ITemplate,
        IExtends<CommonTemplates.RenderableGameObject>,
        ITagged<OrbitTags.Smooth>
{
    [Interpolated]
    Position Position = default;

    [Interpolated]
    Rotation Rotation = default;
    OrbitParams OrbitParams;
    PrefabId PrefabId = new(InterpolationPrefabs.SmoothCube);
}

// Not interpolated — jittery
public partial class RawOrbitEntity
    : ITemplate,
        IExtends<CommonTemplates.RenderableGameObject>,
        ITagged<OrbitTags.Raw>
{
    Position Position = default;
    Rotation Rotation = default;
    OrbitParams OrbitParams;
    PrefabId PrefabId = new(InterpolationPrefabs.RawCube);
}
```

`[Interpolated]` on `Position` and `Rotation` generates `Interpolated<T>` and `InterpolatedPrevious<T>` wrapper components.

## Interpolation functions

Static methods marked `[GenerateInterpolatorSystem]` specify how each component type blends. The source generator emits a Burst-compiled job system for each:

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

`GroupName` lets related interpolators be registered with a single call.

## Setup

The generated `AddInterpolationSampleInterpolators()` extension registers both the previous-frame savers and the blending systems:

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

### Entity creation

`SetInterpolated` initializes the current, previous, and interpolated copies in one call:

```csharp
entity.SetInterpolated(new Position(position));
entity.SetInterpolated(new Rotation(quaternion.identity));
```

Raw entities use plain `Set` — they only have the fixed-update component.

## Rendering

Two aspects — one for the interpolated wrappers, one for the raw components — dispatched per partition:

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
        var go = _goManager.Resolve(view.GameObjectId);
        go.transform.position = (Vector3)view.InterpolatedPosition;
        go.transform.rotation = view.InterpolatedRotation;
    }

    [ForEachEntity(typeof(OrbitTags.Raw))]
    void RenderRaw(in RawOrbitView view)
    {
        var go = _goManager.Resolve(view.GameObjectId);
        go.transform.position = (Vector3)view.Position;
        go.transform.rotation = view.Rotation;
    }

    partial struct SmoothOrbitView
        : IAspect, IRead<Interpolated<Position>, Interpolated<Rotation>, GameObjectId> { }

    partial struct RawOrbitView : IAspect, IRead<Position, Rotation, GameObjectId> { }
}
```

Because `Position` and `Rotation` are `[Unwrap]`, the aspect exposes `view.InterpolatedPosition` (`float3`) and `view.InterpolatedRotation` (`quaternion`) directly — no double-`.Value`.

## Concepts introduced

- **`[Interpolated]`** on template fields generates `Interpolated<T>` and `InterpolatedPrevious<T>` wrapper components.
- **`[GenerateInterpolatorSystem]`** — source-generates Burst-compiled blending systems from static methods.
- **`GroupName`** — registers a group of interpolators via a single generated extension method.
- **`SetInterpolated()`** — initializes all three component copies (current, interpolated, previous).
- **Reading via an aspect** — `IRead<Interpolated<Position>>` plus `[Unwrap]` gives clean `view.InterpolatedPosition` access.

See [Interpolation](../advanced/interpolation.md) for the full reference. For a manual `SimPosition` + lerp alternative, see [Feeding Frenzy](07-feeding-frenzy.md).
