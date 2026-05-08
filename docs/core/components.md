# Components

Components are plain data containers attached to entities. In Trecs, components are **unmanaged structs** — no classes, no managed references, no garbage collection.

## Defining Components

```csharp
public partial struct Health : IEntityComponent
{
    public float Current;
    public float Max;
}
```

Every component must:

1. Be a **`partial struct`** so the source generator can extend it
2. Be **unmanaged** (no reference types, strings, or managed arrays)
3. Implement **`IEntityComponent`**

### The `[Unwrap]` Shorthand

For single-field components, use `[Unwrap]` to expose the inner value directly in [aspects](../data-access/aspects.md):

```csharp
[Unwrap]
public partial struct Position : IEntityComponent
{
    public float3 Value;
}

[Unwrap]
public partial struct Speed : IEntityComponent
{
    public float Value;
}
```

With `[Unwrap]`, aspect properties return `float3` and `float` directly instead of the wrapper struct.

## Reading and Writing Components

### Single Entity Access

```csharp
// Read
ref readonly Health hp = ref World.Component<Health>(entityIndex).Read;

// Write
ref Health hp = ref World.Component<Health>(entityIndex).Write;
hp.Current -= damage;

// Via EntityAccessor
var entity = entityIndex.ToEntity(World);
ref Health hp = ref entity.Get<Health>().Write;

// Safe access (check if component exists)
if (World.TryComponent<Health>(entityIndex, out var healthAccessor))
{
    ref readonly Health hp = ref healthAccessor.Read;
}
```

Going through `Read`/`Write` lets Trecs lazily complete any in-flight jobs with conflicting access before handing back the reference. See [Dependency Tracking](../performance/dependency-tracking.md) for the details.

## Component Field Attributes

When declaring components in a [template](templates.md), fields can be annotated to control their update behavior:

```csharp
public partial class PlayerEntity : ITemplate, IHasTags<PlayerTag>
{
    [Interpolated]
    Position Position = default;               // Smoothed between fixed frames

    Velocity Velocity;                          // Plain simulation state — Fixed-only writes

    [VariableUpdateOnly]
    RenderState RenderState;                    // Only readable and writable in variable update

    [Constant]
    PlayerId PlayerId;                          // Immutable after creation

    [Input(MissingInputBehavior.RetainCurrent)]
    MoveInput MoveInput;                        // Player input, retains last value
}
```

| Attribute | Effect |
|-----------|--------|
| `[Interpolated]` | Generates interpolation components for smooth rendering. See [Interpolation](../advanced/interpolation.md). |
| `[VariableUpdateOnly]` | Component is render-only state — only variable-cadence phases (`Input` / `EarlyPresentation` / `Presentation` / `LatePresentation`) may read or write it. `Fixed` systems may not touch it. Asserted at the access site — see [Accessor Roles](../advanced/accessor-roles.md#capability-matrix) for the full role × access matrix. |
| `[Constant]` | Component is immutable after entity creation. Asserted at the write site. |
| `[Input(...)]` | Marks component as input data. See [Input System](../advanced/input-system.md). |

> Components without any attribute are treated as **simulation state**: any phase may read them, but only `Fixed` systems may write.

## Global Entity

Every world has a single **global entity** for storing world-wide state. Access it via `GlobalComponent<T>()`:

```csharp
// Read
ref readonly Score score = ref World.GlobalComponent<Score>().Read;

// Write
ref Score score = ref World.GlobalComponent<Score>().Write;
score.Value += 10;
```

To declare which components the global entity has, see [Global Entity Template](templates.md#global-entity-template).
