# Jobs & Burst

Trecs integrates with Unity's job system and Burst compiler for parallel, high-performance entity processing.

## WrapAsJob — The Simplest Approach

Mark a static `[ForEachEntity]` method with `[WrapAsJob]` to generate a Burst-compiled parallel job:

```csharp
public partial class ParticleMoveSystem : ISystem
{
    [ForEachEntity(Tag = typeof(SampleTags.Particle))]
    [WrapAsJob]
    static void Execute(in Velocity velocity, ref Position position, in NativeWorldAccessor world)
    {
        position.Value += world.DeltaTime * velocity.Value;
    }
}
```

The source generator creates the job struct, scheduling method, and [dependency tracking](dependency-tracking.md) automatically. The method must be `static` because it is called from inside a job and must use `NativeWorldAccessor` instead of `WorldAccessor`

## Manual Job Structs

For more control, define a job struct yourself with a `[ForEachEntity]` Execute method:

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

The Trecs Source Generator will add a ScheduleParallel extension method for your job struct that handles the iteration and component access.

This gives you control over the job struct fields (e.g., passing precomputed values like `DeltaTime`), while the source generator still handles scheduling and dependency tracking.

However note that you can also use [`[PassThroughArgument]`](../core/systems.md#passthroughargument) on fields of a `[WrapAsJob]` method to achieve the same effect without defining a job struct.

## NativeWorldAccessor

`NativeWorldAccessor` is the job-safe counterpart to `WorldAccessor`. It supports structural operations with a `sortKey` parameter for deterministic ordering:

```csharp
// In a job:
nativeWorld.AddEntity<GameTags.Bullet>(sortKey: (uint)index)
    .Set(new Position(pos));

nativeWorld.RemoveEntity(entityIndex);
nativeWorld.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

!!! warning "Structural changes are fixed-phase only"
    `AddEntity`, `RemoveEntity`, and `MoveTo` on `NativeWorldAccessor` are **only valid from jobs scheduled by fixed-update systems**. Calling them from variable-phase or input-phase jobs will assert in debug builds. This keeps simulation state changes deterministic and aligned with submission boundaries.

### Sort Keys

When `RequireDeterministicSubmission` is enabled, sort keys determine the order structural operations are applied. Use entity IDs or loop indices as sort keys for reproducible results.

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

For advanced job features — `[FromWorld]` auto-wiring, native component access types, native set operations, and external job tracking — see [Advanced Job Features](../advanced/advanced-jobs.md).
