# Jobs & Burst

Trecs integrates with [Unity's job system and Burst compiler](https://docs.unity3d.com/Packages/com.unity.burst@latest) for parallel, high-performance entity processing. You can write job structs by hand — Trecs's source generator handles the scheduling boilerplate (`ScheduleParallel`, [dependency tracking](dependency-tracking.md), field auto-wiring). For the simplest cases, the generator emits the entire job struct from a single annotated method.

## `[WrapAsJob]` — the simplest approach

Mark a `static` `[ForEachEntity]` method with `[WrapAsJob]` and Trecs generates a Burst-compiled parallel job for you:

```csharp
public partial class ParticleMoveSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Particle))]
    [WrapAsJob]
    static void Execute(
        in Velocity velocity,
        ref Position position,
        in NativeWorldAccessor world)
    {
        position.Value += world.DeltaTime * velocity.Value;
    }
}
```

Two requirements:

- **`static`** — the generated Burst job calls your method directly, with no system instance, so it must be `static`.
- **Use `NativeWorldAccessor`, not `WorldAccessor`** for any world-level reads (`DeltaTime`, structural ops, set ops) inside the body.

Trecs schedules the job instead of calling your `Execute()` method directly.

[`[PassThroughArgument]`](../core/systems.md#passthroughargument) is supported here too and forwards the data to the generated job.

## Manual job structs

To define a custom job struct instead of using `[WrapAsJob]`, put `[ForEachEntity]` on the job's `Execute` method — Trecs generates a `ScheduleParallel` for it the same way:

```csharp
public partial class ParticleJobSystem : ISystem
{
    [BurstCompile]
    partial struct MoveJob
    {
        public float DeltaTime;

        [ForEachEntity(typeof(SampleTags.Particle))]
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

`[ForEachEntity]` is **not** required. You can write an entirely hand-rolled `IJobFor` / `IJobParallelFor` and still pass Trecs data into it. See [Advanced Job Features](../advanced/advanced-jobs.md).

## Per-iteration `EntityHandle`

A `[ForEachEntity]` callback inside a job can take an `EntityHandle` parameter alongside its components — works in both `[WrapAsJob]` and manual job structs:

```csharp
[ForEachEntity(typeof(SampleTags.Particle))]
[WrapAsJob]
static void Cull(ref Lifetime lifetime, EntityHandle handle, in NativeWorldAccessor world)
{
    if (lifetime.Remaining <= 0)
        world.RemoveEntity(handle);
}
```

The handle is materialized per iteration from a per-group buffer the source generator wires up automatically — no dictionary lookup, one indexed read. `EntityAccessor` is **not** supported in jobs (it carries a managed `WorldAccessor` reference); use `EntityHandle` plus `NativeWorldAccessor` for per-entity ops.

## `NativeWorldAccessor`

`NativeWorldAccessor` is the Burst-compatible counterpart to `WorldAccessor`. Get one with `world.ToNative()` (or take it as an `in NativeWorldAccessor` parameter — `[WrapAsJob]` auto-injects it):

```csharp
// In a job:
nativeWorld.AddEntity<GameTags.Bullet>(sortKey: (uint)index)
    .Set(new Position(pos));

nativeWorld.RemoveEntity(entityIndex);
nativeWorld.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

## Thread-safety cheat sheet

| Operation | Main thread | Jobs |
|-----------|-------------|------|
| Read a single component | `world.Component<T>(idx).Read` | `NativeComponentRead<T>` (single entity), `NativeComponentBufferRead<T>` (one group), `NativeComponentLookupRead<T>` (across groups) |
| Write a single component | `world.Component<T>(idx).Write` | `NativeComponentWrite<T>`, `NativeComponentBufferWrite<T>`, `NativeComponentLookupWrite<T>` |
| Add / remove / move entity | `world.Set<T>().Deferred` (deferred until next frame) | `NativeWorldAccessor` (deferred until next frame + sort key) |
| Read a set | `world.Set<T>().Read` | `NativeSetRead<T>` |
| Mutate a set | `world.Set<T>().Write` | `NativeSetCommandBuffer<T>` (deferred but only until job completion) |

!!! warning
    `WorldAccessor` is **main-thread only**. Inside jobs always use `NativeWorldAccessor` and the native read/write types — see [Advanced Job Features](../advanced/advanced-jobs.md) for how to wire them via `[FromWorld]`.

For deeper coverage — `[FromWorld]` auto-wiring, cross-group lookup types, native set operations, `[GlobalIndex]`, external job-handle tracking — see [Advanced Job Features](../advanced/advanced-jobs.md). For the parallel iteration pattern itself, see [Queries & Iteration](../data-access/queries-and-iteration.md).
