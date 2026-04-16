# Entity Events

Entity events let you react to structural changes — when entities are added to a group, removed from a group, or moved between groups.

## Subscribing to Events

The recommended pattern is to use `[ForEachEntity]` on your event callback method. The source generator handles iterating over the affected entities with proper component access:

```csharp
public partial class RemoveCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new();

    public RemoveCleanupHandler(World world)
    {
        World = world.CreateAccessor();

        World.Events
            .InGroupsWithTags<FrenzyTags.Fish>()
            .OnRemoved(OnFishRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnFishRemoved(in TargetMeal targetMeal)
    {
        if (targetMeal.Value.Exists(World))
        {
            World.RemoveEntity(targetMeal.Value);
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
```

The `[ForEachEntity]` attribute generates the iteration code — your callback receives component access for each affected entity, just like in a system.

## Event Types

| Event | Trigger |
|-------|---------|
| `OnAdded` | Entities added to a matching group |
| `OnRemoved` | Entities removed from a matching group |
| `OnMoved` | Entities moved from one group to another |

## Using Aspects in Event Callbacks

You can use aspects for bundled component access, just like in systems:

```csharp
public partial class CleanupHandlers : IDisposable
{
    readonly GameObjectRegistry _gameObjectRegistry;
    readonly DisposeCollection _disposables = new();

    public CleanupHandlers(World world, GameObjectRegistry gameObjectRegistry)
    {
        World = world.CreateAccessor();
        _gameObjectRegistry = gameObjectRegistry;

        World.Events
            .InGroupsWithTags<SampleTags.Prey>()
            .OnRemoved(OnPreyRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnPreyRemoved(in Prey prey)
    {
        var go = _gameObjectRegistry.Resolve(prey.GameObjectId);
        GameObject.Destroy(go);
        _gameObjectRegistry.Unregister(prey.GameObjectId);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }

    partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
}
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

Use `AddTo` with a `DisposeCollection` to manage subscription lifetimes:

```csharp
readonly DisposeCollection _disposables = new();

World.Events
    .InGroupsWithTags<GameTags.Enemy>()
    .OnRemoved(OnEnemyRemoved)
    .AddTo(_disposables);

// Clean up all subscriptions
_disposables.Dispose();
```

Frame event subscriptions return an `IDisposable` directly:

```csharp
IDisposable sub = World.Events.OnSubmission(() => { ... });
sub.Dispose();
```
