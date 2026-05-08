# Jobs & Burst

Trecs integrates with [Unity's job system and Burst compiler](https://docs.unity3d.com/Packages/com.unity.burst@latest) for parallel, high-performance entity processing. The source generator emits the job struct, the schedule call, and [dependency tracking](dependency-tracking.md) so you don't write any of that boilerplate yourself.

## `[WrapAsJob]` — the simplest approach

Mark a `static` `[ForEachEntity]` method with `[WrapAsJob]` and Trecs generates a Burst-compiled parallel job for you:

```csharp
public partial class ParticleMoveSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Particle))]
    [WrapAsJob]
    static void MoveParticles(
        in Velocity velocity,
        ref Position position,
        in NativeWorldAccessor world)
    {
        position.Value += world.DeltaTime * velocity.Value;
    }

    public void Execute() => MoveParticles();
}
```

Two requirements:

- **`static`** — calling instance state (e.g. `World`) from inside a Burst job is unsafe; the C# compiler enforces this at the call site instead of inside generated code.
- **Use `NativeWorldAccessor`, not `WorldAccessor`**, for any world-level reads (`DeltaTime`, structural ops, set ops) inside the body.

Calling the wrapped method (`MoveParticles()` above) invokes the generated schedule call, not the user method directly.

## Manual job structs

When you need fields on the job (e.g. precomputed values, native arrays, lookups), declare a `[BurstCompile] partial struct` with a `[ForEachEntity]` Execute method and let the generator add a `ScheduleParallel` extension:

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

For most cases you can stay on `[WrapAsJob]` and use [`[PassThroughArgument]`](../core/systems.md#passthroughargument) to forward values into the generated job — see [Advanced Job Features](../advanced/advanced-jobs.md) for `[FromWorld]` field wiring, lookups, and `[GlobalIndex]`.

## `NativeWorldAccessor`

`NativeWorldAccessor` is the Burst-compatible counterpart to `WorldAccessor`. Get one with `world.ToNative()` (or take it as an `in NativeWorldAccessor` parameter — `[WrapAsJob]` auto-injects it). Structural ops take a `sortKey` so concurrent writes apply in deterministic order:

```csharp
// In a job:
nativeWorld.AddEntity<GameTags.Bullet>(sortKey: (uint)index)
    .Set(new Position(pos));

nativeWorld.RemoveEntity(entityIndex);
nativeWorld.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

!!! warning "Structural changes are simulation-only"
    `AddEntity`, `RemoveEntity`, and `MoveTo` are only allowed from jobs scheduled by systems with [`AccessorRole.Fixed`](../advanced/accessor-roles.md) (the default for `[ExecuteIn(SystemPhase.Fixed)]`, including the implicit default). Calling them from any presentation- or input-phase job asserts in debug builds. Read-only ops (entity resolution, shared-pointer resolution) are allowed from any role.

### Sort keys

When [`RequireDeterministicSubmission`](../core/world-setup.md) is enabled, `sortKey` determines the application order of buffered structural ops. Use the iteration index or a stable entity-derived value — anything reproducible across runs.

## Thread-safety cheat sheet

| Operation | Main thread | Jobs |
|-----------|-------------|------|
| Read a single component | `world.Component<T>(idx).Read` | `NativeComponentRead<T>` (single entity), `NativeComponentBufferRead<T>` (one group), `NativeComponentLookupRead<T>` (across groups) |
| Write a single component | `world.Component<T>(idx).Write` | `NativeComponentWrite<T>`, `NativeComponentBufferWrite<T>`, `NativeComponentLookupWrite<T>` |
| Add / remove / move entity | `WorldAccessor` (deferred) | `NativeWorldAccessor` (deferred + sort key, sim only) |
| Read a set | `world.Set<T>().Read` | `NativeSetRead<T>` |
| Mutate a set | `world.Set<T>().Write` | `NativeSetWrite<T>` (deferred) |

!!! warning
    `WorldAccessor` is **main-thread only**. Inside jobs always use `NativeWorldAccessor` and the native read/write types — see [Advanced Job Features](../advanced/advanced-jobs.md) for how to wire them via `[FromWorld]`.

For deeper coverage — `[FromWorld]` auto-wiring, lookup types across groups, native set operations, `[GlobalIndex]`, external job-handle tracking — continue to [Advanced Job Features](../advanced/advanced-jobs.md). For the parallel iteration pattern itself, see [Queries & Iteration](../data-access/queries-and-iteration.md).
