# Jobs & Burst

Trecs integrates with [Unity's job system and Burst compiler](https://docs.unity3d.com/Packages/com.unity.burst@latest) for parallel, high-performance entity processing. The source generator emits the job struct, the schedule call, and [dependency tracking](dependency-tracking.md) so you don't write any of that boilerplate yourself.

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

- **`static`** — calling instance state (e.g. `World`) from inside a Burst job is unsafe; the C# compiler enforces this at the call site instead of inside generated code.
- **Use `NativeWorldAccessor`, not `WorldAccessor`**, for any world-level reads (`DeltaTime`, structural ops, set ops) inside the body.

Then, instead of calling your system `Execute()` method directly, Trecs will schedule a job instead, and inside the job it will call your static method.

Note that [`[PassThroughArgument]`](../core/systems.md#passthroughargument) is supported in this case as well and will forward the data to the generated job.

## Manual job structs

In some cases you might want to define a custom Job instead of always using `WrapAsJob`.  You can do this just by using `[ForEachEntity]` on your job `Execute()` method:

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

Then the Trecs source will generate a ScheduleParallel method that you can call and pass in `World`

See [Advanced Job Features](../advanced/advanced-jobs.md) for `[FromWorld]` field wiring, lookups, and `[GlobalIndex]`.

## `NativeWorldAccessor`

`NativeWorldAccessor` is the Burst-compatible counterpart to `WorldAccessor`. Get one with `world.ToNative()` (or take it as an `in NativeWorldAccessor` parameter — `[WrapAsJob]` auto-injects it). Structural ops take a `sortKey` so concurrent writes apply in deterministic order:

```csharp
// In a job:
nativeWorld.AddEntity<GameTags.Bullet>(sortKey: (uint)index)
    .Set(new Position(pos));

nativeWorld.RemoveEntity(entityIndex);
nativeWorld.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

!!! warning "Structural changes must match template cadence"
    `AddEntity`, `RemoveEntity`, and `MoveTo` require an accessor whose role matches the target template's cadence:

    - **Normal templates:** require [`AccessorRole.Fixed`](../advanced/accessor-roles.md) (the default for `[ExecuteIn(SystemPhase.Fixed)]`, including the implicit default). Calling them from a presentation- or input-phase job asserts in debug builds.
    - **`[VariableUpdateOnly]` templates** (e.g. cameras, view-only helpers): the rule is inverted — structural changes require an `AccessorRole.Variable` accessor (presentation/input phases) and are rejected from Fixed-role jobs.

    `AccessorRole.Unrestricted` bypasses both rules. 

### Sort keys

When [`RequireDeterministicSubmission`](../core/world-setup.md) is enabled, `sortKey` determines the application order of buffered structural ops. Use the iteration index or a stable entity-derived value — anything reproducible across runs.

## Thread-safety cheat sheet

| Operation | Main thread | Jobs |
|-----------|-------------|------|
| Read a single component | `world.Component<T>(idx).Read` | `NativeComponentRead<T>` (single entity), `NativeComponentBufferRead<T>` (one group), `NativeComponentLookupRead<T>` (across groups) |
| Write a single component | `world.Component<T>(idx).Write` | `NativeComponentWrite<T>`, `NativeComponentBufferWrite<T>`, `NativeComponentLookupWrite<T>` |
| Add / remove / move entity | `WorldAccessor` (deferred) | `NativeWorldAccessor` (deferred + sort key) |
| Read a set | `world.Set<T>().Read` | `NativeSetRead<T>` |
| Mutate a set | `world.Set<T>().Write` | `NativeSetWrite<T>` (deferred) |

!!! warning
    `WorldAccessor` is **main-thread only**. Inside jobs always use `NativeWorldAccessor` and the native read/write types — see [Advanced Job Features](../advanced/advanced-jobs.md) for how to wire them via `[FromWorld]`.

For deeper coverage — `[FromWorld]` auto-wiring, lookup types across groups, native set operations, `[GlobalIndex]`, external job-handle tracking — continue to [Advanced Job Features](../advanced/advanced-jobs.md). For the parallel iteration pattern itself, see [Queries & Iteration](../data-access/queries-and-iteration.md).
