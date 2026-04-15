# Advanced Topics

## Component Interpolation

Trecs supports interpolating component values between fixed-update steps for smooth rendering. This is essential when your simulation runs at a fixed timestep but rendering happens at a variable frame rate.

### Setup

Mark interpolated fields in your template:

```csharp
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [Interpolated]
    public CPosition Position;
}
```

The source generator creates interpolation jobs that run automatically. During variable update, you can access the interpolated values for rendering.

## Burst/Jobs Integration

### IJobSystem

For high-performance parallel processing, implement `IJobSystem`:

```csharp
public partial class ParallelMovementSystem : IJobSystem
{
    public void Execute(JobHandle inputDeps)
    {
        // Schedule Unity jobs
        // Return the combined job handle
    }
}
```

### Native Entity Operations

For Burst-compiled jobs that need to interact with entities, use native operation types:

- `NativeEntityOps` -- Schedule entity add/remove from within jobs
- `NativeEntityLookup<T>` -- Look up component data by entity reference in jobs

These types are Burst-compatible and can be used inside `IJobParallelFor` and similar Unity job types.

### NativeSharedPtr

For sharing data between managed and Burst code:

```csharp
var ptr = new NativeSharedPtr<MyData>(Allocator.Persistent);
ptr.Value = new MyData { ... };

// Pass to jobs
var job = new MyJob { Data = ptr };
```

## Entity Filters

Filters provide efficient, reusable queries for subsets of entities:

```csharp
// Create a filter
var filterId = Ecs.CreateFilter();

// Add/remove entities from the filter
Ecs.AddToFilter(filterId, entityIndex);
Ecs.RemoveFromFilter(filterId, entityIndex);

// Query filtered entities
var filtered = Ecs.GetFilteredEntities(filterId);
```

Filters are useful when you need to track a dynamic subset of entities that doesn't correspond to a tag combination.

## Serialization

Trecs supports serializing and deserializing the entire ECS state:

```csharp
// Serialize
var serializer = new EcsSerializer();
serializer.Serialize(world);
byte[] data = serializer.GetBytes();

// Deserialize
var deserializer = new EcsDeserializer();
deserializer.Deserialize(world, data);
```

This is useful for save/load systems, network synchronization, and replay functionality.

## Input Queuing

The input system allows buffering inputs separately from the simulation:

```csharp
// Mark a component as input
public partial class MyEntity : ITemplate, ITags<MyTag>
{
    [Input]
    public CPlayerInput Input;
}

// Use [InputSystem] for systems that process input
[InputSystem]
public partial class InputProcessor : ISystem
{
    [ForEachEntity]
    void Execute(ref CPlayerInput input)
    {
        // Process buffered input
    }
}
```

Input components are submitted through a separate queue, allowing the simulation to process them at the correct time. This is critical for:

- Network games with input delay
- Replay systems
- Deterministic simulation with variable input timing

## Memory Management

### Heap Types

Trecs provides several heap types for different allocation patterns:

| Type | Use Case |
|------|----------|
| `UniquePtr<T>` | Single-owner managed objects, disposed with the entity |
| `SharedPtr<T>` | Reference-counted managed objects |
| `NativeSharedPtr<T>` | Burst-compatible reference-counted data |

### Blob Cache

For large, immutable data structures shared across entities:

```csharp
var blobId = world.BlobCache.Allocate(myLargeData);

// Access in systems
var data = Ecs.GetBlob<MyData>(blobId);
```

Blobs are reference-counted and automatically freed when no longer used.

## Deterministic Simulation

Trecs is designed for deterministic simulation:

- **Fixed timestep**: Systems in the fixed-update phase run at a constant delta time
- **Separate RNG streams**: `Ecs.FixedRandom` and `Ecs.VariableRandom` provide deterministic random numbers that don't interfere with each other
- **Stable entity IDs**: Entity unique IDs are assigned deterministically
- **Batched submission**: Entity creation/destruction is batched to avoid order-dependent behavior

## Entity Events

Subscribe to entity lifecycle events for reactive behavior:

```csharp
// In your system
Ecs.Events
    .OnEntityAdded<EnemyTag>((ref CHealth health, ref CPosition pos) =>
    {
        // React to enemy creation
    })
    .OnEntityRemoved<EnemyTag>((ref CHealth health) =>
    {
        // React to enemy destruction (cleanup, drop loot, etc.)
    });
```

Events fire during entity submission, after structural changes are applied.

## Global Entity

The global entity is a singleton for world-wide state that doesn't belong to any specific entity:

```csharp
// Access global components
var worldState = Ecs.GetGlobalComponent<CWorldState>();
ref var config = ref Ecs.GetGlobalComponentMut<CGameConfig>();
```

Define global components in a template with a dedicated tag, and register it as a global entity in the world builder.
