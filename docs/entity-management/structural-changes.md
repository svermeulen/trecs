# Structural Changes

Add, remove, and move (re-tag) operations are **deferred** — they're queued during system execution and applied later at a submission boundary. This keeps entity indices stable during iteration and is what makes safe parallel processing possible.

In the examples below, `World` is the [`WorldAccessor`](../advanced/accessor-roles.md) injected into a system.

## When Submission Happens

The submission drains the queues of entity operations. The system runner calls it for you automatically at the end of each frame (that is - both at the end of every fixed update and at the end of `World.Tick()`).

You may also call it manually via `World.SubmitEntities()` (on `WorldAccessor`)

## Deferred Operations

### Adding entities

```csharp
World.AddEntity<GameTags.Bullet>()
    .Set(new Position(pos))
    .Set(new Velocity(vel));
```

The entity is buffered and joins its group on the next submission. The `EntityIndex` is not assigned until then.

### Removing entities

```csharp
World.RemoveEntity(entityIndex);
World.RemoveEntity(entityHandle);
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Moving entities (partition transitions)

```csharp
// Move to the group with these destination tags. Component data is preserved.
World.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

The type parameters are the **destination** tag set, not the from/to pair. The entity's group changes; its component data is copied across.

## Conflict Resolution

When the same entity has multiple operations queued in a single submission, Trecs resolves them with these rules:

- **Remove beats move.** If an entity has both a remove and a move queued (from either managed or native queues), the remove wins.
- **First move wins.** If two systems both queue a move for the same entity, only the first is applied; later moves are dropped silently.
- **Remove is idempotent.** Queuing the same remove twice is safe — only one removal happens.
- **Cascading submission.** If an observer fires during submission and queues more changes (e.g. an `OnAdded` handler spawning a child), Trecs runs additional submission iterations until the queues drain — bounded by `WorldSettings.MaxSubmissionIterations` (default 10).

To react to submission boundaries, see [Entity Events — Frame Events](entity-events.md#frame-events).

## Deterministic Submission

For replay and networking, enable deterministic ordering:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true,
};
```

This sorts structural operations queued from Burst jobs (via [`NativeWorldAccessor`](../performance/jobs-and-burst.md)) before applying them, making submission order independent of job-thread interleaving. Operations queued from the main thread via `WorldAccessor` are already deterministic — they're applied in the order user code queued them — so this setting only affects the native path. The cost is a single sort per submission — cheap enough to enable by default if you may ever record, replay, or network the simulation.

When you queue structural changes from a Burst job through [`NativeWorldAccessor`](../performance/jobs-and-burst.md), pass a `sortKey` to control the deterministic order:

```csharp
nativeAccessor.AddEntity<MyTag>(sortKey: (uint)i);  // i = the iteration index in the job
```
