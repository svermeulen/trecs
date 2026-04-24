# Entity Events

Entity events let you react to structural changes — when entities are added, removed, or moved between partitions. Callbacks are invoked during [submission](structural-changes.md#when-submission-happens), after the queued structural changes have been applied.

## Subscribing to Events

The recommended pattern is to use `[ForEachEntity]` on your event callback method. The source generator handles iterating over the affected entities with proper component access:

```csharp
public partial class RemoveCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container (see note below)

    public RemoveCleanupHandler(World world)
    {
        World = world.CreateAccessor();

        World.Events
            .EntitiesWithTags<FrenzyTags.Fish>()
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
            .EntitiesWithTags<SampleTags.Prey>()
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

## Disposing Subscriptions

The `OnRemoved`, `OnAdded`, and `OnMoved` methods return an `IDisposable` that you can use to clean up the subscription when it's no longer needed.

```csharp
IDisposable sub = World.Events.OnSubmission(() => { ... });
sub.Dispose();
```

Note that Trecs does not include a `DisposeCollection` type — you can use a simple wrapper like the one in the samples, or any `IDisposable` container of your choice.  Or just a `List<IDisposable>` and call `Dispose` on each item in `Dispose()`.

## Scoping Events

Events can be scoped to specific groups:

```csharp
// By tags
World.Events.EntitiesWithTags<GameTags.Player>()

// By multiple tags
World.Events.EntitiesWithTags<GameTags.Player, GameTags.Active>()

// By components
World.Events.EntitiesWithComponents<Health>()

// By specific group
World.Events.InGroup(specificGroup)

// All groups
World.Events.AllEntities()
```

## Frame Events

Subscribe to simulation lifecycle events:

```csharp
World.Events.OnSubmissionStarted(() =>
{
    // Submission is about to begin
});

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

World.Events.OnPostApplyInputs(() =>
{
    // Inputs have been applied for this fixed step —
    // runs inside the fixed phase, before systems execute
});
```

