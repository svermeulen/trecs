# Entity Events

Entity events let you react to structural changes — when entities are added to a group, removed from a group, or moved between groups.

## Subscribing to Events

Use `World.Events` to build event subscriptions:

```csharp
World.Events
    .InGroupsWithTags<GameTags.Enemy>()
    .OnAdded((group, range, world) =>
    {
        for (int i = range.Start; i < range.End; i++)
        {
            var entityIndex = new EntityIndex(i, group);
            // Initialize newly added enemy
        }
    })
    .OnRemoved((group, range, world) =>
    {
        for (int i = range.Start; i < range.End; i++)
        {
            // Clean up removed enemy
        }
    });
```

## Event Types

| Event | Trigger |
|-------|---------|
| `OnAdded` | Entities added to a matching group |
| `OnRemoved` | Entities removed from a matching group |
| `OnMoved` | Entities moved from one group to another |

### OnMoved

```csharp
World.Events
    .InGroupsWithTags<BallTags.Ball>()
    .OnMoved((fromGroup, toGroup, range, world) =>
    {
        // Ball transitioned between Active/Resting states
    });
```

## Scoping Events

Events can be scoped to specific groups:

```csharp
// By tags
World.Events.InGroupsWithTags<GameTags.Player>()

// By multiple tags
World.Events.InGroupsWithTags<GameTags.Player, GameTags.Active>()

// By components
World.Events.InGroupsWithComponents<Health>()

// By specific group
World.Events.InGroup(specificGroup)

// All groups
World.Events.InAllGroups()
```

## EntityRange

Event callbacks receive an `EntityRange` describing the contiguous range of affected entity indices:

```csharp
.OnAdded((group, range, world) =>
{
    // range.Start — first affected index (inclusive)
    // range.End   — last affected index (exclusive)
    // range.Count — number of affected entities

    for (int i = range.Start; i < range.End; i++)
    {
        var entityIndex = new EntityIndex(i, group);
        ref readonly Position pos = ref world.Component<Position>(entityIndex).Read;
    }
})
```

## Priority

Control the order in which event handlers fire:

```csharp
World.Events
    .InGroupsWithTags<GameTags.Enemy>()
    .WithPriority(10)  // Higher priority = fires first
    .OnAdded((group, range, world) => { ... });
```

## Frame Events

Subscribe to simulation lifecycle events:

```csharp
World.Events.OnSubmission(() =>
{
    // All structural changes applied
});

World.Events.OnFixedUpdateStarted(() =>
{
    // Fixed update phase is beginning
});

World.Events.OnFixedUpdateCompleted(() =>
{
    // Fixed update phase is complete
});

World.Events.OnVariableUpdateStarted(() =>
{
    // Variable update phase is beginning
});
```

## Disposing Subscriptions

Event subscriptions are `IDisposable` — dispose them to unsubscribe:

```csharp
var subscription = World.Events
    .InGroupsWithTags<GameTags.Enemy>()
    .OnAdded((group, range, world) => { ... });

// Later:
subscription.Dispose();
```

Frame event subscriptions return an `IDisposable` directly:

```csharp
IDisposable sub = World.Events.OnSubmission(() => { ... });
sub.Dispose();
```
