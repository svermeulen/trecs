# Components

Components are plain data attached to entities. They're **unmanaged structs** — no classes, no managed references, no garbage collection.

## Defining components

```csharp
public partial struct Health : IEntityComponent
{
    public float Current;
    public float Max;
}
```

A component must:

1. Be a `partial struct` (so the source generator can extend it)
2. Be unmanaged (no reference types, strings, or managed arrays)
3. Implement `IEntityComponent`

## The `[Unwrap]` shorthand

For single-field components, `[Unwrap]` exposes the inner value directly through [aspects](../data-access/aspects.md):

```csharp
[Unwrap]
public partial struct Position : IEntityComponent
{
    public float3 Value;
}
```

Inside an aspect that reads `Position`, `aspect.Position` returns a `float3` rather than the wrapping struct.

## Reading and writing components

The most common access happens through [aspects](../data-access/aspects.md) and `[ForEachEntity]` parameters — those are the patterns you'll use day-to-day. For ad-hoc access by `EntityIndex`:

```csharp
ref readonly Health hp = ref World.Component<Health>(entityIndex).Read;

ref Health hpW = ref World.Component<Health>(entityIndex).Write;
hpW.Current -= damage;
```

The `.Read` / `.Write` split lets Trecs lazily complete any in-flight jobs with conflicting access before handing back the reference. See [Dependency Tracking](../performance/dependency-tracking.md).

When the entity may not have the component, use the safe `TryComponent` form:

```csharp
if (World.TryComponent<Health>(entityIndex, out var healthAccessor))
{
    ref readonly Health hp = ref healthAccessor.Read;
    // ...
}
```

For a single-entity view bundling several components, see [`EntityAccessor`](entities.md#accessing-entity-data).

## Component field attributes

When components are declared in a [template](templates.md), fields can be annotated to control update behavior:

```csharp
public partial class PlayerEntity : ITemplate, ITagged<PlayerTag>
{
    [Interpolated]
    Position Position = default;               // Smoothed between fixed frames

    Velocity Velocity;                          // Plain simulation state — Fixed-only writes

    [VariableUpdateOnly]
    RenderState RenderState;                    // Render-only state

    [Constant]
    PlayerId PlayerId;                          // Immutable after creation

    [Input(MissingInputBehavior.RetainCurrent)]
    MoveInput MoveInput;                        // Player input
}
```

| Attribute | Effect |
|-----------|--------|
| `[Interpolated]` | Generates interpolation companion components for smooth rendering. See [Interpolation](../advanced/interpolation.md). |
| `[VariableUpdateOnly]` | Only variable-cadence phases (Input / Presentation) may read or write it. Asserted at the access site — see [Accessor Roles](../advanced/accessor-roles.md#capability-matrix). |
| `[Constant]` | Immutable after entity creation. Asserted at the write site. |
| `[Input(...)]` | Marks the component as input data. See [Input System](input-system.md). |

A component without an attribute is **simulation state**: any phase may read it, but only `Fixed` systems may write it.

## Global entity

Every world has a single **global entity** for world-wide state. Access it with `GlobalComponent<T>()`:

```csharp
ref readonly Score score = ref World.GlobalComponent<Score>().Read;

ref Score scoreW = ref World.GlobalComponent<Score>().Write;
scoreW.Value += 10;
```

To declare which components the global entity has, see [Global Entity Template](templates.md#global-entity-template).
