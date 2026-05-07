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
        World = world.CreateAccessor(AccessorRole.Fixed);

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
        World = world.CreateAccessor(AccessorRole.Fixed);
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

## Cascading Structural Changes from Callbacks

Observer callbacks may themselves queue more structural changes — e.g. an `OnRemoved` handler that removes a follower entity, or an `OnAdded` handler that spawns a child. Trecs handles cascades **iteratively, not recursively**:

- Each submission "iteration" snapshots the queued operations, applies them, and fires the matching observers. Any new ops queued from those callbacks land in a fresh buffer and get processed in the *next* iteration of the same `SubmitEntities()` call.
- This continues until the queues drain, bounded by [`WorldSettings.MaxSubmissionIterations`](structural-changes.md#conflict-resolution) (default 10). Hitting the cap throws `"possible circular submission detected"` in `DEBUG` builds, which usually means an observer is producing changes that re-trigger itself indefinitely.
- Because cascading is iterative, callback ordering across iterations is fully deterministic: each iteration walks groups and observers in the same fixed order, and native (Burst-queued) operations are sorted at the boundary when [`RequireDeterministicSubmission`](structural-changes.md#deterministic-submission) is enabled.
- Calling `world.SubmitEntities()` from inside a callback is **not** allowed — it asserts on a re-entrancy guard. Just queue the structural change and let the running submission cascade pick it up.

The [strict-accessor-during-Fixed-execute rule](../advanced/accessor-roles.md#strict-accessor-during-fixed-execute-rule) does **not** apply inside observer callbacks: the rule short-circuits whenever no `Fixed`-role system is currently inside its `Execute`, and submission runs *between* system executes. Callbacks may therefore use a separately-created service accessor — this is what enables patterns like the [pointer-cleanup `OnRemoved` sample](../samples/10-pointers.md), where a service holds its own `AccessorRole.Fixed` accessor and reads components from the cleanup callback.

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

