# Advanced Job Features

This page covers advanced job APIs for manual job scheduling and low-level component access. For basics of `[WrapAsJob]` and manual job structs, see [Jobs & Burst](../performance/jobs-and-burst.md).

## FromWorld — Auto-Wiring Job Fields

`[FromWorld]` marks fields on a job struct to be auto-populated from the world before scheduling:

```csharp
[BurstCompile]
partial struct MyJob : IJobFor
{
    [FromWorld(typeof(GameTags.Player))]
    public NativeComponentBufferRead<Position> Positions;

    [FromWorld(typeof(GameTags.Player))]
    public NativeComponentBufferWrite<Velocity> Velocities;

    [FromWorld]
    public NativeWorldAccessor NativeWorld;

    public void Execute(int index)
    {
        Velocities[index].Value += new float3(0, -9.8f, 0) * NativeWorld.DeltaTime;
    }
}
```

### Supported field types

| Field Type | Purpose | Requires Tag? |
|-----------|---------|---------------|
| `NativeComponentBufferRead<T>` | Read-only component buffer for a group | Yes |
| `NativeComponentBufferWrite<T>` | Writable component buffer for a group | Yes |
| `NativeComponentLookupRead<T>` | Read-only lookup across multiple groups | Yes |
| `NativeComponentLookupWrite<T>` | Writable lookup across multiple groups | Yes |
| `NativeSetRead<TSet>` | Read-only set access | No |
| `NativeSetCommandBuffer<TSet>` | Writable set access | No |
| `NativeWorldAccessor` | Job-safe world operations | No |
| `GroupIndex` | Runtime handle for the resolved group | Yes |

### Tag resolution

Fields that require a tag scope (buffers, lookups, `GroupIndex`) can get their tags two ways:

- **Inline** — specify `Tag` or `Tags` on the attribute: `[FromWorld(typeof(GameTags.Player))]`. The tag is baked into the generated code.
- **At schedule time** — omit `Tag`/`Tags`, and the generated `ScheduleParallel` includes a `TagSet` parameter the caller must provide:

```csharp
[BurstCompile]
partial struct FlexibleJob : IJobFor
{
    [FromWorld]  // No inline tag — becomes a schedule parameter
    public NativeComponentBufferWrite<Position> Positions;

    public void Execute(int index) { ... }
}

// Caller provides the tag at schedule time
new FlexibleJob().ScheduleParallel(accessor, TagSet<GameTags.Player>.Value);
```

Useful when the tagset isn't known until runtime, or when reusing the same job struct for multiple tag scopes. `ScheduleParallel` gets one parameter per `[FromWorld]` field without an inline tag.

Fields that don't require tags (`NativeSetRead`, `NativeSetCommandBuffer`, `NativeWorldAccessor`) are populated automatically and never generate schedule parameters.

## Native component access

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

For accessing components on arbitrary entities across groups. Lookups are wired into the job struct via `[FromWorld]` and consumed by generated aspect code — you rarely index into them directly. Prefer the higher-level `<Aspect>.NativeFactory` pattern (see [Aspects](../data-access/aspects.md)), which hides the lookup plumbing behind the same aspect interface you use on the main thread.

## Native set operations

Sets can be read and modified from jobs:

```csharp
[FromWorld]
NativeSetRead<HighlightedParticle> highlightedRead;

[FromWorld]
NativeSetCommandBuffer<HighlightedParticle> highlightedWrite;

// Modify (queued, applied after the writer's SetFlushJob — same frame, before any
// reader job that depends on the set)
highlightedWrite.Add(handle, world);
highlightedWrite.Remove(handle, world);
highlightedWrite.Clear();   // Order-insensitive: wins over any Add/Remove in this writer cycle.

// Read-side iteration is via TryGetGroupEntry on a per-group basis.
```

## External job tracking

When a job is scheduled manually without the Trecs source generator (e.g., a third-party job or custom scheduling pattern), register it with the world so the [dependency tracker](../performance/dependency-tracking.md) knows about it, via `TrackExternalJob` on the system's `WorldAccessor`:

```csharp
JobHandle handle = myJob.Schedule(count, batchSize);

accessor.TrackExternalJob(handle)
    .Writes<Position>(TagSet<GameTags.Player>.Value)
    .Reads<Velocity>(TagSet<GameTags.Player>.Value);
```

To force-complete tracked jobs before main-thread access:

```csharp
accessor.SyncMainThread<Position>(group);
```

Note that this is very low level operation that shouldn't be necessary in most cases.

## Thread-safety cheat sheet

| Operation | Main thread | Jobs |
|-----------|-------------|------|
| Read a single component | `handle.Component<T>(world).Read` | `NativeComponentRead<T>` (single entity), `NativeComponentBufferRead<T>` (one group), `NativeComponentLookupRead<T>` (across groups) |
| Write a single component | `handle.Component<T>(world).Write` | `NativeComponentWrite<T>`, `NativeComponentBufferWrite<T>`, `NativeComponentLookupWrite<T>` |
| Add / remove / partition-transition entity | `world.AddEntity<T>()` / `handle.Remove(world)` / `handle.SetTag<T>(world)` / `handle.UnsetTag<T>(world)` | `NativeWorldAccessor` (queued until next submission; pass `sortKey` for deterministic ordering) |
| Read a set | `world.Set<T>().Read` | `NativeSetRead<T>` |
| Mutate a set | `world.Set<T>().Write` | `NativeSetCommandBuffer<T>` (deferred but only until job completion) |

## See also

- [Sample 05 — Job System](../samples/05-job-system.md): the basic `[WrapAsJob]` pattern with structural changes.
- [Sample 07 — Feeding Frenzy](../samples/07-feeding-frenzy.md): multiple iteration styles compared side by side, all using jobs.
