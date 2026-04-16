# 05 — Job System

Parallel entity processing with Unity's job system and Burst compiler. Shows both main-thread and job-based approaches side by side.

**Source:** `Samples/05_JobSystem/`

## What It Does

Particles spawn, move, and bounce off boundaries. A toggle switches between main-thread and Burst-compiled parallel job execution. Arrow keys adjust particle count.

## Schema

### Components

`Position`, `Velocity`, `Rotation`, `UniformScale`, `ColorComponent`, `GameObjectId` from Common, plus global components for configuration:

```csharp
public struct DesiredNumParticles : IEntityComponent { public int Value; }
public struct IsJobsEnabled : IEntityComponent { public bool Value; }
```

### Templates

```csharp
public partial class ParticleEntity : ITemplate,
    IExtends<CommonTemplates.Renderable>,
    IHasTags<SampleTags.Particle>
{
    public Velocity Velocity;
}

public partial class Globals : ITemplate, IExtends<TrecsTemplates.Globals>
{
    public DesiredNumParticles DesiredNumParticles;
    public IsJobsEnabled IsJobsEnabled;
}
```

## Systems

### ParticleMoveSystem — Job vs Main Thread

The system defines a Burst-compiled job struct with `[ForEachEntity]`:

```csharp
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
```

In `Execute()`, the system chooses between job and main-thread paths:

```csharp
public void Execute()
{
    if (isJobsEnabled)
    {
        new MoveJob { DeltaTime = World.DeltaTime }.ScheduleParallel(World);
    }
    else
    {
        MoveMainThread();
    }
}

[ForEachEntity(Tag = typeof(SampleTags.Particle))]
void MoveMainThread(in Velocity velocity, ref Position position)
{
    position.Value += World.DeltaTime * velocity.Value;
}
```

### ParticleSpawnerSystem — Job-Based Entity Creation

When jobs are enabled, uses `NativeWorldAccessor` with reserved entity handles for parallel spawning:

```csharp
// Reserve handles on main thread
var handles = World.ReserveEntityHandles(count, Allocator.TempJob);

// Schedule spawn job
new SpawnJob
{
    NativeWorld = World.ToNative(),
    ReservedHandles = handles,
    // ...
}.Schedule(count, 64);
```

Inside the job:

```csharp
nativeWorld.AddEntity<SampleTags.Particle>(
    sortKey: (uint)index,
    reservedRef: reservedHandles[index])
    .Set(new Position(pos))
    .Set(new Velocity(vel))
    .AssertComplete();
```

## Concepts Introduced

- **`[BurstCompile]`** on job structs for SIMD-optimized code
- **`[ForEachEntity]` on job methods** — source generator creates the parallel iteration
- **`ScheduleParallel(World)`** — generated scheduling method with automatic dependency tracking
- **`NativeWorldAccessor`** for structural operations in jobs
- **`ReserveEntityHandles`** for pre-allocating stable handles before parallel creation
- **Sort keys** for deterministic ordering of job-created entities
- **Main-thread fallback** — same logic can run on main thread or in jobs
