# Components

Components are plain data containers attached to entities. In Trecs, components are **unmanaged structs** — no classes, no managed references, no garbage collection.

## Defining Components

```csharp
public struct Health : IEntityComponent
{
    public float Current;
    public float Max;
}
```

Every component must:

1. Be an **unmanaged struct** (no reference types, strings, or arrays)
2. Implement **`IEntityComponent`**

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
ref readonly Position pos = ref world.Component<Position>(entityIndex).Read;

// Write
ref Position pos = ref world.Component<Position>(entityIndex).Write;
pos.Current -= damage;

// Via EntityAccessor
var entity = entityIndex.ToEntity(world);
ref Health hp = ref entity.Get<Health>().Write;

// Safe access (check if component exists)
if (world.TryComponent<Health>(entityIndex, out var healthAccessor))
{
    ref readonly Health hp = ref healthAccessor.Read;
}
```

Accessing via Read/Write properties allows Trecs to lazily complete any jobs with conflicting access before providing the reference. See [Dependency Tracking](../performance/dependency-tracking.md) for details on how this works.

## Component Field Attributes

When declaring components in a [template](templates.md), fields can be annotated with attributes that control their update behavior:

```csharp
public partial class PlayerEntity : ITemplate, IHasTags<PlayerTag>
{
    [Interpolated]
    Position Position = default;               // Smoothed between fixed frames

    [FixedUpdateOnly]
    Velocity Velocity;                          // Only writable in fixed update

    [VariableUpdateOnly]
    RenderState RenderState;                    // Only writable in variable update

    [Constant]
    PlayerId PlayerId;                          // Immutable after creation

    [Input(MissingInputFrameBehaviour.RetainCurrent)]
    MoveInput MoveInput;                        // Player input, retains last value
}
```

| Attribute | Effect |
|-----------|--------|
| `[Interpolated]` | Generates interpolation components for smooth rendering. See [Interpolation](../advanced/interpolation.md). |
| `[FixedUpdateOnly]` | Component is only writable during fixed update phase |
| `[VariableUpdateOnly]` | Component is only writable during variable update phase |
| `[Constant]` | Component is immutable after entity creation |
| `[Input(...)]` | Marks component as input data. See [Input System](../advanced/input-system.md). |

## Global Entity

Every world has a single **global entity** for storing world-wide state. Access it via `GlobalComponent<T>()`:

```csharp
// Read global component
ref readonly Score score = ref world.GlobalComponent<Score>().Read;

// Write global component
ref Score score = ref world.GlobalComponent<Score>().Write;
score.Value += 10;
```

To define which components the global entity has, see [Global Entity Template](templates.md#global-entity-template).
