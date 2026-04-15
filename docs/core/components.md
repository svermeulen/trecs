# Components

Components are plain data containers attached to entities. In Trecs, components are **unmanaged structs** — no classes, no managed references, no garbage collection.

## Defining Components

```csharp
[TypeId(12345)]
public struct Health : IEntityComponent
{
    public float Current;
    public float Max;
}
```

Every component must:

1. Be an **unmanaged struct** (no reference types, strings, or arrays)
2. Implement **`IEntityComponent`**
3. Have a **`[TypeId]`** attribute with a unique integer ID (required for serialization)

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
ref readonly Position pos = ref ecs.Component<Position>(entityIndex).Read;

// Write
ref Position pos = ref ecs.Component<Position>(entityIndex).Write;
pos.Current -= damage;

// Via EntityAccessor
var entity = entityIndex.ToEntity(ecs);
ref Health hp = ref entity.Get<Health>().Write;

// Safe access (check if component exists)
if (ecs.TryComponent<Health>(entityIndex, out var healthAccessor))
{
    ref readonly Health hp = ref healthAccessor.Read;
}
```

### Buffer Access (All Entities in a Group)

```csharp
var buffer = ecs.ComponentBuffer<Position>(group);
for (int i = 0; i < buffer.Count; i++)
{
    ref Position pos = ref buffer[i];
    pos.Value.y += 1f;
}
```

## Component Field Attributes

When declaring components in a [template](templates.md), fields can be annotated with attributes that control their update behavior:

```csharp
public partial class PlayerEntity : ITemplate, IHasTags<PlayerTag>
{
    [Interpolated]
    public Position Position = Position.Default;      // Smoothed between fixed frames

    [FixedUpdateOnly]
    public Velocity Velocity;                          // Only writable in fixed update

    [VariableUpdateOnly]
    public RenderState RenderState;                    // Only writable in variable update

    [Constant]
    public PlayerId PlayerId;                          // Immutable after creation

    [Input(MissingInputFrameBehaviour.RetainCurrent)]
    public MoveInput MoveInput;                        // Player input, retains last value
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

Every world has a single **global entity** for storing world-wide state:

```csharp
// Read global component
ref readonly Score score = ref ecs.GlobalComponent<Score>().Read;

// Write global component
ref Score score = ref ecs.GlobalComponent<Score>().Write;
score.Value += 10;
```

To add components to the global entity, extend `TrecsTemplates.Globals` in a template:

```csharp
public partial class MyGlobals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    public Score Score;
    public GameConfig Config;
}
```
