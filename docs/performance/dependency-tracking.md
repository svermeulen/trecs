# Dependency Tracking

Trecs tracks which jobs read and write which data and inserts the right `JobHandle` chain on your behalf. You never call `JobHandle.CombineDependencies` or pass handles between jobs — the framework infers them from the types you declare on jobs and the `.Read` / `.Write` properties you use on the main thread.

## Why this exists

Unity's job system makes dependency wiring your problem: every job needs the right input handle, and forgetting to chain them correctly leads to either race conditions or silent serialization. Trecs reads your access pattern (parameter `in`/`ref` modifiers, the native field types listed in [Jobs & Burst](jobs-and-burst.md#thread-safety-cheat-sheet)) and emits the wiring at schedule time.

## The reader/writer model

Each piece of data follows a standard reader/writer rule:

- **Multiple readers** run in parallel.
- **A writer is exclusive** — it waits for all current readers and the previous writer to finish before starting.

Granularity is **per (component type, group)**. A job writing `Position` for `Fish` entities does *not* block a job reading `Position` for `Player` entities — they live in different groups. (See [Groups & TagSets](../advanced/groups-and-tagsets.md).)

| Job A | Job B | Run in parallel? |
|-------|-------|---|
| Reads `Position` (Fish) | Reads `Position` (Fish) | Yes |
| Reads `Position` (Fish) | Writes `Position` (Player) | Yes — different group |
| Reads `Position` (Fish) | Writes `Position` (Fish) | No — writer waits |
| Writes `Position` (Fish) | Writes `Position` (Fish) | No — writer waits |

The same rule applies to [sets](../entity-management/sets.md): `NativeSetRead` / `NativeSetWrite` are tracked per set type. Deferred set ops on `WorldAccessor` / `NativeWorldAccessor` don't synchronize — they apply at submission, after every job is complete.

## How dependencies get declared

The source generator inspects each job:

- Iteration parameters: `in T` reads `T`, `ref T` writes `T`.
- Native fields / parameters: the type itself encodes intent (`NativeComponentBufferRead<T>` vs `NativeComponentBufferWrite<T>`, etc.).

The generated `ScheduleParallel` then:

1. Waits on every outstanding job that conflicts with the declared access.
2. Registers the new job so subsequent schedules see it as outstanding.

You only call the generated method — the rest is handled.

## Main-thread sync

Main-thread access through `WorldAccessor` lazily completes only the conflicting jobs:

- **`.Read`** — completes outstanding writers (readers keep running).
- **`.Write`** — completes outstanding writers **and** readers.

```csharp
// Completes jobs currently writing Position for this group;
// jobs that only read Position keep running.
ref readonly var pos = ref world.Component<Position>(entityIndex).Read;

// Completes jobs reading OR writing Position for this group.
ref var posMut = ref world.Component<Position>(entityIndex).Write;
```

That lazy sync is why you never call `JobHandle.Complete()` yourself — touching the data is the sync point.

!!! tip
    Unintentional main-thread access mid-phase forces the in-flight job to complete and stalls the worker threads. If a system mixes main-thread and job access on the same component, schedule jobs first and only `.Read` / `.Write` after the work for that component is done — or move the main-thread work to a different system that the scheduler can order around the job system.

## Phase boundaries

The five [update phases](../core/systems.md#update-phases) — `Input`, `Fixed`, `EarlyPresentation`, `Presentation`, `LatePresentation` — each end with a full job fence: every outstanding job completes before the next phase begins. So:

- Fixed-phase jobs finish before any presentation system runs.
- Within a phase, mix job and main-thread systems freely — the tracker orders them.
- Cross-phase reads never need manual sync.

## Summary

| Mechanism | When | What it syncs |
|---|---|---|
| Generated `ScheduleParallel` | Job scheduling | Waits on conflicting jobs; registers new access |
| `.Read` | Main-thread component access | Completes outstanding writers |
| `.Write` | Main-thread component access | Completes outstanding writers + readers |
| Phase boundary | Between update phases | Completes everything |
