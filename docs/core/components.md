# Components

Components are plain data attached to entities — **unmanaged structs**, no classes, no managed references, no garbage collection.

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

The source generator extends each component with:

- `Equals` and `==` / `!=` overloads that compare the struct as raw bytes — equivalent to `memcmp`, so equality is fast and fixed-cost regardless of field count.
- `[System.Serializable]` so Unity's `SerializedObject` machinery can navigate the type's fields. This is what lets the [Trecs entity inspector](../editor-windows/hierarchy.md) render component values when an entity is selected in the Hierarchy.
- A constructor for `[Unwrap]` components that takes the inner value directly (so you can write `new Speed(5f)` instead of `new Speed { Value = 5f }`).

These are the reasons components must be `partial`.

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

Most access goes through [aspects](../data-access/aspects.md) and `[ForEachEntity]` parameters. For ad-hoc access by `EntityHandle`:

```csharp
ref readonly Health hp = ref handle.Component<Health>(World).Read;

ref Health hpW = ref handle.Component<Health>(World).Write;
hpW.Current -= damage;
```

The `.Read` / `.Write` split lets Trecs lazily complete any in-flight jobs with conflicting access before handing back the reference. See [Dependency Tracking](../performance/dependency-tracking.md).

For optional components, use the matching `TryComponent` overload — it returns `false` if the entity no longer exists or lacks that component:

```csharp
if (handle.TryComponent<Velocity>(World, out var velAccessor))
{
    ref readonly Velocity vel = ref velAccessor.Read;
}
```

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

    [Input(MissingInputBehavior.Retain)]
    MoveInput MoveInput;                        // Player input
}
```

| Attribute | Effect |
|-----------|--------|
| `[Interpolated]` | Generates interpolation companion components for smooth rendering. See [Interpolation](../advanced/interpolation.md). |
| `[VariableUpdateOnly]` | Only phases that run with the `Variable` accessor role (Input and the three Presentation phases) may read or write it. Fixed-update systems cannot touch it. Asserted at the access site — see [Accessor Roles](../advanced/accessor-roles.md#capability-matrix). |
| `[Constant]` | Immutable after entity creation. Asserted at the write site. |
| `[Input(...)]` | Marks the component as input data. See [Input System](input-system.md). |

A component with no attribute is **simulation state**: any phase may read it; only `Fixed` systems may write it.

## Global entity

Every world has a single **global entity** for world-wide state. Access it with `GlobalComponent<T>()`:

```csharp
ref readonly Score score = ref World.GlobalComponent<Score>().Read;

ref Score scoreW = ref World.GlobalComponent<Score>().Write;
scoreW.Value += 10;
```

To declare which components the global entity has, see [Global Entity Template](templates.md#global-entity-template).
