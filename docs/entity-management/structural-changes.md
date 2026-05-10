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

The entity is buffered and joins its group on the next submission. The `EntityIndex` is not assigned until then.

### Remove

```csharp
World.RemoveEntity(entityIndex);
World.RemoveEntity(entityHandle);
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Move (partition transition)

```csharp
// Move to the group with these destination tags. Component data is preserved.
World.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
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

For replay and networking, enable deterministic ordering:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true,
};
```

This sorts structural operations queued from Burst jobs (via [`NativeWorldAccessor`](../performance/jobs-and-burst.md)) before applying them, making submission order independent of job-thread interleaving. Operations queued from the main thread are already deterministic — they apply in the order user code queued them — so this setting only affects the native path. The cost is a single sort per submission, cheap enough to enable by default if you may ever record, replay, or network the simulation.

When you queue structural changes from a Burst job through `NativeWorldAccessor`, pass a `sortKey` to control the deterministic order:

```csharp
nativeAccessor.AddEntity<MyTag>(sortKey: (uint)i);  // i = the iteration index in the job
```
