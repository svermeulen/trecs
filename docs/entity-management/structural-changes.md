# Structural Changes

Entity creation, removal, and partition transitions are **deferred operations** — they are queued during system execution and applied at submission boundaries.

## Why Deferred?

Applying structural changes immediately during iteration would not allow for safe concurrent processing, since this would invalidate entity indices. Instead, Trecs queues all changes and applies them in a controlled batch at the end of each update phase.

## When Submission Happens

Structural changes are applied:

1. **After each fixed update iteration** — `World.Tick()` calls `SubmitEntities()` after each fixed timestep step
2. **At the end of `World.Tick()`** — any remaining changes are submitted
3. **Manually** — `world.SubmitEntities()` can be called explicitly if needed

## Deferred Operations

### Adding Entities

```csharp
World.AddEntity<GameTags.Bullet>()
    .Set(new Position(pos))
    .Set(new Velocity(vel));
```

The entity is buffered and added to its group at the next submission.

### Removing Entities

```csharp
World.RemoveEntity(entityIndex);
World.RemoveEntity(entityHandle);
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Moving Entities (Partition Transitions)

```csharp
World.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

Moving changes the entity's tag combination, which moves it to a different group. The component data is preserved.

## Conflict Resolution

When multiple operations are queued for the same entity within a single submission, Trecs applies these rules:

- **Remove supersedes move** — if an entity is scheduled for both removal and a move (from either the managed or native queues), the removal wins and the move is discarded.
- **First move wins** — if multiple moves are queued for the same entity (e.g., two systems both call `MoveTo`), only the first move is applied. Subsequent moves are silently dropped.
- **Remove is idempotent** — queuing multiple removes for the same entity is safe; only one removal is performed.
- **Group removes cascade** — `RemoveEntitiesWithTags<T>()` cancels any queued moves for entities in matching groups (remove supersedes move).
- **Multiple submission iterations** — if structural changes during submission trigger further changes (e.g., an [`OnAdded`](entity-events.md) handler creates more entities), Trecs runs additional submission iterations until stable, up to `WorldSettings.MaxSubmissionIterations`.

!!! note
    These rules apply at the *queue* level within a single submission. Operations across different submissions never conflict — they are fully resolved before the next one begins.

To react to submission events, see [Entity Events — Frame Events](entity-events.md#frame-events).

## Deterministic Submission

For replay and networking, enable deterministic ordering:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true
};
```

This sorts structural operations by a deterministic key before applying them, ensuring identical results across runs. The performance cost is very small — just a sort of the queued operations at each submission — so it's reasonable to enable by default if your game may ever need replay or networking.

When using `NativeWorldAccessor` in jobs, the `sortKey` parameter controls the ordering:

```csharp
nativeAccessor.AddEntity<MyTag>(sortKey: entityId);
```
