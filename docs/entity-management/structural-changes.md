# Structural Changes

Add, remove, and partition-transition operations are **deferred** — queued during system execution and applied later at a submission boundary. This keeps component buffers and entity indices stable during iteration and enables safe parallel processing.

In the examples below, `World` is the [`WorldAccessor`](../advanced/accessor-roles.md) injected into a system.

## When submission happens

Submission drains the queued operations. The system runner calls it automatically at the end of every fixed update and at the end of `World.LateTick()` — see the [per-frame phase diagram](../core/systems.md#phase-diagram).

Call it manually via `World.Submit()` (though note this shouldn't be necessary normally)

## Deferred operations

### Add

```csharp
World.AddEntity<GameTags.Bullet>()
    .Set(new Position(pos))
    .Set(new Velocity(vel));
```

The entity is buffered and placed into storage on the next submission. Its storage location isn't assigned until then.

### Remove

```csharp
// By stable handle (the canonical removal entry point)
entityHandle.Remove(World);

// From inside a system loop that has an aspect in hand
enemyAspect.Remove(World);

// Bulk by tag
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Partition transition (`SetTag` / `UnsetTag`)

Partition transitions are expressed by mutating one tag at a time on an entity. The submitter resolves the new storage location from the entity's current tags plus the requested changes.  Component values are copied across unchanged.

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

**Which verb to use:**

- **`SetTag<T>`** is valid for any partition-variant tag — both shapes:
    - Presence/absence dim (arity 1): turns the tag *on*.
    - Multi-variant dim (arity ≥ 2): switches the active variant in that dim. Other dims are preserved.
- **`UnsetTag<T>`** is valid **only** on presence/absence dims (arity 1). Multi-variant dims have no defined "absent" partition, so calling `UnsetTag` on one throws. To switch a multi-variant dim use `SetTag` for the new variant.

`SetTag<T>` is a no-op (and silently coalesced away) if the entity already has the requested tag.

The Burst-side equivalents — `handle.SetTag<T>(nativeWorld)` and `handle.UnsetTag<T>(nativeWorld)` — take a `NativeWorldAccessor` and match the managed signatures otherwise. See [Jobs & Burst](../performance/jobs-and-burst.md#nativeworldaccessor).

## Same-frame coalescing

Multiple `SetTag` / `UnsetTag` calls on the same entity in a single submission **coalesce into one structural move**:

```csharp
// Two systems both touch the same fish in the same fixed tick.
fish.SetTag<MoveState.Running>(World);   // dim A: switch to Running
fish.SetTag<FrenzyTags.Hungry>(World);   // dim B: turn Hungry on
// → One move at submit time: both tag changes applied together.
```

Distinct partition dimensions stack freely. An entity can change variant in dim A, turn dim B on, and turn dim C off in one submission — all three resolve to a single move.

## Conflict resolution

When the same entity has multiple operations queued in a single submission:

- **Remove always wins** Once an entity is queued for removal, any subsequent `SetTag` / `UnsetTag` for that entity in the same submission is silently dropped — the entity won't exist when submission finishes anyway.
- **Same-dim collisions.** Two `SetTag` / `UnsetTag` ops touching the same partition dimension on the same entity in one submission are a conflict. Debug builds throw; release builds keep the op with the highest `Tag.Value` and log an error.
- **Remove is idempotent.** Queuing the same remove twice is safe.
- **Cascading submission.** If an observer queues more changes during submission (e.g. an `OnAdded` handler spawning a child), Trecs runs additional submission iterations until the queues drain — bounded by `WorldSettings.MaxSubmissionIterations` (default 10).

To react to submission boundaries, see [Entity Events — Frame Events](entity-events.md#frame-events).

## Deterministic submission

Trecs always sorts structural operations queued from Burst jobs (via [`NativeWorldAccessor`](../performance/jobs-and-burst.md)) before applying them, so submission order doesn't depend on job-thread interleaving.
