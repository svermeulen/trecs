# Structural Changes

Entity creation, removal, and state transitions are **deferred operations** — they are queued during system execution and applied at submission boundaries.

## Why Deferred?

Applying structural changes immediately during iteration would invalidate indices and corrupt buffers. Instead, Trecs queues all changes and applies them in a controlled batch:

```csharp
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball)
{
    if (ball.Velocity.Length < threshold)
    {
        // This is safe — queued, not applied immediately
        World.MoveTo<BallTags.Ball, BallTags.Resting>(ball.EntityIndex);
    }
}
```

## When Submission Happens

Structural changes are applied:

1. **After each fixed update iteration** — `World.Tick()` calls `SubmitEntities()` after each fixed timestep step
2. **At the end of `Tick()`** — any remaining changes are submitted
3. **Manually** — `ecs.SubmitEntities()` can be called explicitly if needed

## Deferred Operations

### Adding Entities

```csharp
World.AddEntity<GameTags.Bullet>()
    .Set(new Position(pos))
    .Set(new Velocity(vel))
    .AssertComplete();
```

The entity is buffered and added to its group at the next submission.

### Removing Entities

```csharp
World.RemoveEntity(entityIndex);
World.RemoveEntity(entityHandle);
World.RemoveEntitiesWithTags<GameTags.Bullet>();
```

### Moving Entities (State Transitions)

```csharp
World.MoveTo<BallTags.Ball, BallTags.Resting>(entityIndex);
```

Moving changes the entity's tag combination, which moves it to a different group. The component data is preserved.

## Conflict Resolution

When multiple operations are queued for the same entity:

- **Remove supersedes Move** — if an entity is scheduled for both removal and a move, the removal wins
- **Multiple submissions** — if structural changes during submission trigger further changes (e.g., an `OnAdded` handler creates more entities), Trecs runs additional submission iterations until stable, up to `WorldSettings.MaxSubmissionIterations`

## Submission Events

Subscribe to know when submission completes:

```csharp
World.Events.OnSubmission(() =>
{
    // All structural changes have been applied
});

World.Events.OnSubmissionStarted(() =>
{
    // Submission is about to begin
});
```

## Deterministic Submission

For replay and networking, enable deterministic ordering:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true
};
```

This sorts structural operations by a deterministic key before applying them, ensuring identical results across runs. When using `NativeWorldAccessor` in jobs, the `sortKey` parameter controls the ordering:

```csharp
nativeAccessor.AddEntity<MyTag>(sortKey: entityId);
```
