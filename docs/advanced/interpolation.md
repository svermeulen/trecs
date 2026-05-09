# Interpolation

Trecs runs simulation at a fixed timestep (default 1/60s), but rendering happens at the variable frame rate. Interpolation smooths component values between fixed frames to prevent visual stuttering.

## How it works

1. **Before each fixed update:** the previous component value is saved (`InterpolatedPrevious<T>`).
2. **During fixed update:** systems update the component normally.
3. **During variable update:** the interpolated value is computed by blending previous → current based on how far through the fixed frame we are (`Interpolated<T>`).
4. **Rendering:** read `Interpolated<T>` for smooth visuals.

## Setup

### 1. Mark fields as interpolated

In your template, use the `[Interpolated]` attribute:

```csharp
public partial class SmoothEntity : ITemplate, ITagged<MyTags.Smooth>
{
    [Interpolated]
    Position Position = default;

    [Interpolated]
    Rotation Rotation = default;

    OrbitParams OrbitParams;
}
```

This automatically generates three components per interpolated field: `Position`, `Interpolated<Position>`, and `InterpolatedPrevious<Position>`.

### 2. Define interpolation functions

Write static methods with `[GenerateInterpolatorSystem]` that define how each component type should be blended. The source generator creates a Burst-compiled job system for each:

```csharp
public static class MyInterpolators
{
    const string GroupName = "MyGameInterpolators";

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
        // nlerp is cheaper than slerp and sufficient for small angular deltas
        result.Value = math.nlerp(a.Value, b.Value, t);
    }
}
```

`GroupName` groups related interpolators so they can be registered together.

### 3. Register with WorldBuilder

The source generator creates an extension method named `Add{GroupName}` that registers all interpolators in the group — both the previous-frame savers and the variable-update blending systems:

```csharp
var world = new WorldBuilder()
    .AddTemplate(SmoothEntity.Template)
    .AddMyGameInterpolators()  // Generated
    .Build();
```

### 4. Create entities with interpolated values

Use `SetInterpolated()` to initialize all three components (current, interpolated, previous) in sync:

```csharp
world.AddEntity<MyTags.Smooth>()
    .SetInterpolated(new Position(startPos))
    .SetInterpolated(new Rotation(startRot))
    .Set(new OrbitParams { ... });
```

### 5. Read interpolated values for rendering

In your variable-update rendering system, read from `Interpolated<T>` instead of the raw component:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class RenderSystem : ISystem
{
    [ForEachEntity(typeof(MyTags.Smooth))]
    void Execute(in Interpolated<Position> pos, in Interpolated<Rotation> rot, in GameObjectId id)
    {
        var go = _registry.Resolve(id);
        go.transform.position = (Vector3)pos.Value.Value;
        go.transform.rotation = rot.Value.Value;
    }
}
```

## What gets generated

For each `[GenerateInterpolatorSystem]` method, the source generator produces:

- A **Burst-compiled `IJobFor` system** that runs during variable update, iterating all entities with the component and blending previous → current using your function.
- An **extension method on `WorldBuilder`** (one per group) that registers all `InterpolatedPreviousSaver<T>` instances and interpolator systems in the group.

You write the interpolation math; scheduling, dependency tracking, and registration are handled.

## Best practices

- **Only interpolate visual components** — positions, rotations, scales, colors. Don't interpolate gameplay state like health or ammo.
- **Use `SetInterpolated()` at creation** — ensures all three components start in sync, avoiding a visual pop on the first frame.
- **Group interpolators by project** — use a shared `GroupName` constant so a single `Add{GroupName}()` registers everything.
- **Prefer `nlerp` over `slerp` for rotations** — angular deltas between fixed frames are typically small enough that the difference is imperceptible, and `nlerp` is significantly cheaper.

## See also

- [Sample 09 — Interpolation](../samples/09-interpolation.md): a full interpolation setup with custom blend functions.
