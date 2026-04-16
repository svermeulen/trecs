# Jobs & Burst

Trecs integrates with Unity's job system and Burst compiler for parallel, high-performance entity processing.

## WrapAsJob — The Simplest Approach

Mark a static `[ForEachEntity]` method with `[WrapAsJob]` to generate a Burst-compiled parallel job:

```csharp
public partial class ParticleJobSystem : ISystem
{
    [BurstCompile]
    partial struct MoveJob
    {
        public float DeltaTime;

        [ForEachEntity(Tag = typeof(SampleTags.Particle))]
        public readonly void Execute(in Velocity velocity, ref Position position)
        {
            position.Value += DeltaTime * velocity.Value;
        }
    }

    public void Execute()
    {
        new MoveJob { DeltaTime = World.DeltaTime }.ScheduleParallel(World);
    }
}
```

The source generator creates the job struct, scheduling method, and [dependency tracking](dependency-tracking.md) automatically.

## FromWorld — Auto-Wiring Job Fields

The `[FromWorld]` attribute marks fields on a job struct to be automatically populated from the world before scheduling:

```csharp
[BurstCompile]
partial struct MyJob : IJobFor
{
    [FromWorld(Tag = typeof(GameTags.Player))]
    public NativeComponentBufferRead<Position> Positions;

    [FromWorld(Tag = typeof(GameTags.Player))]
    public NativeComponentBufferWrite<Velocity> Velocities;

    [FromWorld]
    public NativeWorldAccessor NativeWorld;

    public void Execute(int index)
    {
        Velocities[index].Value += new float3(0, -9.8f, 0) * NativeWorld.DeltaTime;
    }
}
```

`[FromWorld]` supports:

| Field Type | Purpose |
|-----------|---------|
| `NativeComponentBufferRead<T>` | Read-only component buffer for a group |
| `NativeComponentBufferWrite<T>` | Writable component buffer for a group |
| `NativeComponentLookupRead<T>` | Read-only lookup across multiple groups |
| `NativeComponentLookupWrite<T>` | Writable lookup across multiple groups |
| `NativeSetRead<TSet>` | Read-only set access |
| `NativeSetWrite<TSet>` | Writable set access |
| `NativeWorldAccessor` | Job-safe world operations |
| `Group` | Group identifier |

Use `Tag` or `Tags` to scope buffer/lookup fields to specific tag groups.

## NativeWorldAccessor

`NativeWorldAccessor` is the job-safe counterpart to `WorldAccessor`. It supports structural operations with a `sortKey` parameter for deterministic ordering:

```csharp
// In a job:
nativeWorld.AddEntity<GameTags.Bullet>(sortKey: (uint)index)
    .Set(new Position(pos));

nativeWorld.RemoveEntity(entityIndex);
nativeWorld.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

### Sort Keys

When `RequireDeterministicSubmission` is enabled, sort keys determine the order structural operations are applied. Use entity IDs or loop indices as sort keys for reproducible results.

## Native Component Access

### Buffers — Single Group

For iterating all entities in one group:

```csharp
NativeComponentBufferRead<Position> positions;   // Read-only
NativeComponentBufferWrite<Velocity> velocities;  // Read-write

// Access by index
ref readonly Position pos = ref positions[i];
ref Velocity vel = ref velocities[i];
```

### Lookups — Cross-Group

For accessing components on arbitrary entities across groups:

```csharp
NativeComponentLookupRead<Health> healthLookup;
NativeComponentLookupWrite<Damage> damageLookup;

// Access by EntityIndex
ref readonly Health hp = ref healthLookup[entityIndex];
ref Damage dmg = ref damageLookup[entityIndex];

// Check existence
if (healthLookup.Exists(entityIndex)) { ... }
if (healthLookup.TryGet(entityIndex, out Health hp)) { ... }
```

## Native Set Operations

Sets can be read and modified from jobs:

```csharp
[FromWorld]
NativeSetRead<HighlightedParticle> highlightedRead;

[FromWorld]
NativeSetWrite<HighlightedParticle> highlightedWrite;

// Check membership
bool isHighlighted = highlightedRead.Exists(entityIndex);

// Modify (thread-safe, immediate within job)
highlightedWrite.AddImmediate(entityIndex);
highlightedWrite.RemoveImmediate(entityIndex);
```

## Thread Safety Rules

| Operation | Main Thread | Jobs |
|-----------|------------|------|
| Read components | `WorldAccessor` | `NativeComponentBufferRead`, `NativeComponentLookupRead` |
| Write components | `WorldAccessor` | `NativeComponentBufferWrite`, `NativeComponentLookupWrite` |
| Add/remove entities | `WorldAccessor` (deferred) | `NativeWorldAccessor` (deferred + sort key) |
| Move entities | `WorldAccessor` (deferred) | `NativeWorldAccessor` (deferred + sort key) |
| Read sets | `SetAccessor.Read` | `NativeSetRead` |
| Write sets | `SetAccessor.Write` | `NativeSetWrite` |

!!! warning
    `WorldAccessor` is **main-thread only**. Always use `NativeWorldAccessor` and native component types inside jobs.

## External Job Tracking

When scheduling jobs manually (without source generation), register them for dependency tracking:

```csharp
JobHandle handle = myJob.Schedule(count, batchSize);

World.TrackExternalJob(handle)
    .Writes<Position>(TagSet<GameTags.Player>.Value)
    .Reads<Velocity>(TagSet<GameTags.Player>.Value);
```

To force-complete jobs before main-thread access:

```csharp
World.SyncMainThread<Position>(group);
```
