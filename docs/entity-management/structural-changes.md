# Structural Changes

Add, remove, and partition-transition operations are **deferred** — queued during system execution and applied later at a submission boundary. This keeps entity indices stable during iteration and enables safe parallel processing.

In the examples below, `World` is the [`WorldAccessor`](../advanced/accessor-roles.md) injected into a system.

## When submission happens

Submission drains the queued operations. The system runner calls it automatically at the end of every fixed update and at the end of `World.LateTick()` — see the [per-frame phase diagram](../core/systems.md#phase-diagram).

Call it manually via `World.Submit()`.

## Deferred operations

### Add

```csharp
World.AddEntity<GameTags.Bullet>()
    .Set(new Position(pos))
    .Set(new Velocity(vel));
```

The entity is buffered and joins its group on the next submission. Its storage location isn't assigned until then.

### Remove

```csharp
// By stable handle (the canonical removal entry point)
entityHandle.Remove(World);

// From inside an iteration callback that takes an EntityHandle parameter
entity.Remove(World);

// From inside a system loop that has an aspect in hand
enemy.Remove(World);

// Bulk by tag
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Partition transition (`SetTag` / `UnsetTag`)

Partition transitions are expressed by mutating one tag at a time on the entity's existing template. The submitter resolves the destination group from the entity's current group plus the tag delta — you never name the source or destination group directly.

```csharp
// By stable handle
handle.SetTag<BallTags.Resting>(World);
handle.UnsetTag<BallTags.Active>(World);

// From inside an iteration callback that takes an EntityHandle parameter
entity.SetTag<BallTags.Resting>(World);
entity.UnsetTag<BallTags.Active>(World);

// From an aspect (source-generated, one overload per accessor type)
ball.SetTag<BallTags.Resting>(World);
ball.UnsetTag<BallTags.Active>(World);
```

The single type parameter names the **tag being toggled**, not a from/to pair. The entity is relocated to whichever group results from applying that one-tag delta; component values are copied across unchanged.

**Which verb to use:**

- **`SetTag<T>`** is valid for any partition-variant tag — both shapes:
    - Presence/absence dim (arity 1): turns the tag *on*.
    - Multi-variant dim (arity ≥ 2): switches the active variant in that dim. Other dims are preserved.
- **`UnsetTag<T>`** is valid **only** on presence/absence dims (arity 1). Multi-variant dims have no defined "absent" partition, so calling `UnsetTag` on one throws. To switch a multi-variant dim use `SetTag` for the new variant.

`SetTag<T>` throws if `T` isn't declared as a partition variant on the entity's template (via `IPartitionedBy<…>`). Plain `ITagged<…>` tags aren't movable — they're fixed at the template level.

`SetTag<T>` is a no-op (and silently coalesced away) if the entity is already in the destination group — useful for idempotent state-machine code that doesn't have to check "am I already eating" before calling `SetTag<Eating>`.

The Burst-side equivalents — `handle.SetTag<T>(nativeWorld)` and `handle.UnsetTag<T>(nativeWorld)` (and the matching `EntityIndex` / aspect overloads) — take a `NativeWorldAccessor` and match the managed signatures otherwise. See [Jobs & Burst](../performance/jobs-and-burst.md#nativeworldaccessor).

## Same-frame coalescing

Multiple `SetTag` / `UnsetTag` calls on the same entity in a single submission **coalesce into one structural move**:

```csharp
// Two systems both touch the same fish in the same fixed tick.
fish.SetTag<MoveState.Running>(World);   // dim A: switch to Running
fish.SetTag<FrenzyTags.Hungry>(World);   // dim B: turn Hungry on
// → One move at submit time: destination = current group with both tag changes applied.
```

The submitter builds up a `FinalTagSet` per entity as ops are queued, and only the final coalesced delta is realized as a group move. This means:

- Distinct partition dimensions stack freely. An entity can change variant in dim A, turn dim B on, and turn dim C off in one submission — all three resolve to a single move into the destination group.
- `SetTag<X>` followed by `UnsetTag<X>` on the **same** dim (or `SetTag<A>` followed by `SetTag<B>` where A and B are sibling variants of the same dim) **throws**. Two ops on one dim have ambiguous ordering — Trecs refuses to silently pick a winner. If a system can legitimately produce both outcomes, decide which one before calling.

## Conflict resolution

When the same entity has multiple operations queued in a single submission:

- **Remove beats `SetTag` / `UnsetTag`.** Once an entity is queued for removal, any subsequent `SetTag` / `UnsetTag` for that entity in the same submission is silently dropped — the entity won't exist when submission finishes anyway.
- **Same-dim collisions throw.** As above — two `SetTag` / `UnsetTag` ops touching the same partition dimension on the same entity in one submission raise an exception. Different dimensions coalesce.
- **Remove is idempotent.** Queuing the same remove twice is safe.
- **Cascading submission.** If an observer queues more changes during submission (e.g. an `OnAdded` handler spawning a child), Trecs runs additional submission iterations until the queues drain — bounded by `WorldSettings.MaxSubmissionIterations` (default 10).

To react to submission boundaries, see [Entity Events — Frame Events](entity-events.md#frame-events).

## Deterministic submission

Trecs always sorts structural operations queued from Burst jobs (via [`NativeWorldAccessor`](../performance/jobs-and-burst.md)) before applying them, so submission order doesn't depend on job-thread interleaving. Main-thread operations apply in queue order. Job-side adds must therefore include a `sortKey`, and you have two ways to issue them:

**Pre-reserved handles** — use when the caller needs to track the entity after the add (subsequent move, remove, or component lookup by handle):

```csharp
// On the main thread, before scheduling the job:
using var refs = world.ReserveEntityHandles(count, Allocator.TempJob);

// In the job:
nativeAccessor.AddEntity<MyTag>(sortKey: (uint)i, refs[i]);
```

**Fire-and-forget** — use when the caller doesn't need a handle (most particles, projectiles, spawn-and-find-by-tag-later patterns). Skip `ReserveEntityHandles` entirely:

```csharp
// In the job — no main-thread setup, no NativeArray to thread through:
nativeAccessor.AddEntity<MyTag>(sortKey: (uint)i)
    .Set(new MyComponent { Value = 42 });
```

The submitter claims an `EntityHandle` for each fire-and-forget add on the main thread *after* the deterministic sort runs, so id assignment follows sort-key order rather than bag-thread arrival order. The entity is still addressable by tag / set / observer queries after submission — the caller just doesn't get a handle back from the add site. The returned `NativeAnonymousEntityInitializer` exposes only `.Set<T>()` (no `.Handle` property), reflecting the API contract.

The two forms compose freely in the same job — mix them per call site based on whether you need the handle.
