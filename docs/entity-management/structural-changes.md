# Structural Changes

Add, remove, and move (re-tag) operations are **deferred** — they're queued during system execution and applied later at a submission boundary. This keeps entity indices stable during iteration and is what makes safe parallel processing possible.

In the examples below, `World` is the [`WorldAccessor`](../advanced/accessor-roles.md) injected into a system.

## When submission happens

Submission drains the queues of entity operations. The system runner calls it for you automatically at the end of every fixed update and at the end of `World.Tick()`.

You can also call it manually via `World.SubmitEntities()`.

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

The type parameters spell out the **destination** tag set — where the entity ends up — not a from/to pair. Behind the scenes, the entity is relocated to the destination group's memory block and all its component values are copied across unchanged.

## Conflict resolution

When the same entity has multiple operations queued in a single submission:

- **Remove beats move.** If both are queued, the remove wins.
- **First move wins.** If two systems queue a move for the same entity, only the first applies; later moves are dropped silently.
- **Remove is idempotent.** Queuing the same remove twice is safe.
- **Cascading submission.** If an observer queues more changes during submission (e.g. an `OnAdded` handler spawning a child), Trecs runs additional submission iterations until the queues drain — bounded by `WorldSettings.MaxSubmissionIterations` (default 10).

To react to submission boundaries, see [Entity Events — Frame Events](entity-events.md#frame-events).

## Deterministic submission

Any workflow that needs the simulation to evolve identically across runs — recording / replay, networked rollback, snapshot-load consistency, reproducible tests, debug-replay tooling — needs deterministic submission ordering, so should include this flag:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true,
};
```

This sorts structural operations queued from Burst jobs (via [`NativeWorldAccessor`](../performance/jobs-and-burst.md)) before applying them, so submission order doesn't depend on job-thread interleaving. Operations queued from the main thread are already deterministic — they apply in the order user code queued them — so the setting only affects the native path. The cost is a single sort per submission, cheap enough to leave on by default if you may ever need a reproducible run.

This is also why, when you queue structural changes from a Burst job through `NativeWorldAccessor`, we require a `sortKey` (so we can maintain deterministic order):

```csharp
nativeAccessor.AddEntity<MyTag>(sortKey: (uint)i);  // i = the iteration index in the job
```
