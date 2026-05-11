# Structural Changes

Add, remove, and move (re-tag) operations are **deferred** — queued during system execution and applied later at a submission boundary. This keeps entity indices stable during iteration and enables safe parallel processing.

In the examples below, `World` is the [`WorldAccessor`](../advanced/accessor-roles.md) injected into a system.

## When submission happens

Submission drains the queued operations. The system runner calls it automatically at the end of every fixed update and at the end of `World.Tick()`.

Call it manually via `World.SubmitEntities()`.

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
// By stable handle (the public removal entry point)
World.RemoveEntity(entityHandle);

// From inside an iteration callback that takes an EntityAccessor
entity.Remove();

// From inside a system loop that has an aspect in hand
enemy.Remove(World);

// Bulk by tag
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Move (partition transition)

```csharp
// By stable handle
World.MoveTo<BallTags.Ball, BallTags.Resting>(handle);

// From inside an iteration callback that takes an EntityAccessor
entity.MoveTo<BallTags.Ball, BallTags.Resting>();

// From an aspect
ball.MoveTo<BallTags.Ball, BallTags.Resting>(World);
```

The type parameters spell out the **destination** tag set, not a from/to pair. The entity is relocated to the destination group's memory block; component values are copied across unchanged.

## Conflict resolution

When the same entity has multiple operations queued in a single submission:

- **Remove beats move.** If both are queued, the remove wins.
- **First move wins.** If two systems queue a move for the same entity, only the first applies; later moves are dropped silently.
- **Remove is idempotent.** Queuing the same remove twice is safe.
- **Cascading submission.** If an observer queues more changes during submission (e.g. an `OnAdded` handler spawning a child), Trecs runs additional submission iterations until the queues drain — bounded by `WorldSettings.MaxSubmissionIterations` (default 10).

To react to submission boundaries, see [Entity Events — Frame Events](entity-events.md#frame-events).

## Deterministic submission

Any workflow that needs the simulation to evolve identically across runs — recording / replay, networked rollback, snapshot-load consistency, reproducible tests — needs deterministic submission ordering, so should include this flag:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true,
};
```

This sorts structural operations queued from Burst jobs (via [`NativeWorldAccessor`](../performance/jobs-and-burst.md)) before applying them, so submission order doesn't depend on job-thread interleaving. Main-thread operations are already deterministic — they apply in queue order — so the setting only affects the native path. Cost is a single sort per submission; cheap enough to leave on by default if reproducibility may ever matter.

This is why queuing structural changes from a Burst job through `NativeWorldAccessor` requires a `sortKey`:

```csharp
nativeAccessor.AddEntity<MyTag>(sortKey: (uint)i);  // i = the iteration index in the job
```
