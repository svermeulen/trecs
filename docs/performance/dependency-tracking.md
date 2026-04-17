# Dependency Tracking

Trecs automatically tracks which jobs read and write which data, and ensures safe concurrent access. You never need to manually manage `JobHandle` dependencies — the framework infers them from your declared access patterns.

## Why This Exists

Unity's job system requires explicit dependency management: you must pass the right `JobHandle` to each job and chain them correctly. Trecs eliminates this burden by inferring dependencies from the types you use — `Read` vs `Write` field types in jobs, and `.Read` vs `.Write` properties on the main thread.

## The Reader/Writer Model

Trecs uses a reader/writer locking model for each piece of data:

- **Multiple readers** can run in parallel — reading the same data concurrently is safe
- **A writer is exclusive** — it waits for all current readers and the previous writer to complete before starting

This is tracked at **per-(component, group)** granularity. A job writing `Position` for `Fish` entities does *not* block a job reading `Position` for `Player` entities, because they belong to different groups.

| Job A | Job B | Can run in parallel? |
|-------|-------|---------------------|
| Reads `Position` (Fish) | Reads `Position` (Fish) | Yes — multiple readers are safe |
| Reads `Position` (Fish) | Writes `Position` (Player) | Yes — different groups |
| Reads `Position` (Fish) | Writes `Position` (Fish) | No — writer waits for reader |
| Writes `Position` (Fish) | Writes `Position` (Fish) | No — writer waits for writer |

The same model applies to immediate operations on [sets](../entity-management/sets.md) — `NativeSetRead` and `NativeSetWrite` are tracked independently per set type. Note that deferred set operations via `WorldAccessor` or `NativeWorldAccessor` do not require synchronization, since they are applied at submission boundaries when all jobs are guaranteed complete.

## How Dependencies Are Declared

When you define a job that uses Trecs components, the source generator inspects your component access (which parameters are `ref` vs `in`, which native fields/parameters use `Read` vs `Write` types) and emits dependency wiring automatically at schedule time. The generated `ScheduleParallel` method:

1. Waits on all conflicting outstanding jobs before scheduling your job
2. Registers your job's access after scheduling, so future jobs will depend on it correctly

This all happens automatically inside the generated method — you just call it and the rest is handled.

## Main-Thread Sync

When main-thread code accesses a component through `WorldAccessor`, Trecs lazily completes only the conflicting jobs before returning the reference:

- **`.Read`** — completes outstanding writers (readers can continue running)
- **`.Write`** — completes outstanding writers *and* all readers

```csharp
// This will complete any jobs currently writing to Position for this group,
// but jobs reading Position can keep running
ref readonly Position pos = ref world.Component<Position>(entityIndex).Read;

// This will complete any jobs reading OR writing Position for this group
ref Position pos = ref world.Component<Position>(entityIndex).Write;
```

This is why component access must all go through `WorldAccessor` — it ensures thread safety automatically without requiring you to call `Complete()` on job handles.

!!! tip
    Unintentional main-thread sync points can hurt performance. If a system accesses a component on the main thread that a prior job is still writing, it forces that job to complete immediately. Structure your systems so that main-thread access and job-based access to the same components don't overlap within the same phase.

## Phase Boundaries

At the boundary between [update phases](../core/systems.md#update-phases) (Input → Fixed → Variable → Late Variable), **all outstanding jobs are completed**. This acts as a full fence, guaranteeing a clean slate for the next phase.

This means:

- Jobs scheduled during fixed update are guaranteed complete before variable update systems run
- You can freely mix job-based and main-thread systems within the same phase — the dependency tracker handles ordering automatically
- Cross-phase data access never requires manual synchronization

## Summary

| Mechanism | When it happens | What it syncs |
|-----------|----------------|---------------|
| Job scheduling | Generated `ScheduleParallel` call | Waits on conflicting jobs, registers new access |
| `.Read` property | Main-thread component access | Completes outstanding writers |
| `.Write` property | Main-thread component access | Completes outstanding writers + all readers |
| Phase boundary | Between update phases | Completes everything |
