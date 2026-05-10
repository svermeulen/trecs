# Entity Events

Entity events let a service react to structural changes — whenever entities are added, removed, or moved between groups. Observer callbacks fire during [submission](structural-changes.md#when-submission-happens), after the queued change has been applied.

## Anatomy of a subscription

Every entity-event subscription is built fluently in three parts: pick the **scope** (which groups to watch), pick the **event** (add / remove / move), and supply a **handler**:

```csharp
World.Events
    .EntitiesWithTags<MyTag>()    // 1. scope
    .OnRemoved(OnEntityRemoved);  // 2. event   (3. handler is the method passed in)
```

### Scopes

A scope picks which groups the subscription watches. One of:

| Method | Matches |
|---|---|
| `EntitiesWithTags<...>()` | Groups whose tag set includes all of the given tags |
| `EntitiesWithComponents<T>()` | Groups whose template declares this component |
| `InGroup(GroupIndex)` | One specific group |
| `AllEntities()` | Every group |

### Events

| Event | Trigger |
|-------|---------|
| `OnAdded` | Entities added to a matching group |
| `OnRemoved` | Entities removed from a matching group |
| `OnMoved` | Entities moved from one group to another |

### Handlers

The recommended pattern is to use a `[ForEachEntity]` method as the event handler:

```csharp
public partial class RemoveCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new();

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
            World.RemoveEntity(targetMeal.Value);
    }

    public void Dispose() => _disposables.Dispose();
}
```

All `[ForEachEntity]` features are supported on event handlers, so for example you can use aspects as well:

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

    public void Dispose() => _disposables.Dispose();

    partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
}
```

Note that components read inside `OnRemoved` are still valid, even though the callback fires *after* removal — removed entities are parked at the end of the backing array (past the active count), so the buffers haven't been cleared yet. The removed components are also contiguous in memory, which is cache-friendly for cleanup.

## Priorities

Call `WithPriority(int)` before `OnAdded` / `OnRemoved` / `OnMoved` to control firing order across observers on the same group (higher = later; default `0`):

```csharp
World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .WithPriority(10)
    .OnRemoved(OnBulletRemoved);
```

## Disposing subscriptions

The returned subscription objects all implement `IDisposable`. Dispose the subscription to unregister the handler.

```csharp
var sub = World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .OnRemoved(OnBulletRemoved);
// ...
sub.Dispose();
```

Trecs doesn't ship a `DisposeCollection` type to aggregate subscription disposables together. The samples define a small helper, but a `List<IDisposable>` walked during your object cleanup works fine too.

## Cascading structural changes from callbacks

A callback can itself queue structural changes — e.g. an `OnRemoved` handler that removes a follower, or an `OnAdded` handler that spawns a child.  Trecs keeps processing the queue until empty or a maximum number of iterations is reached - configurable via `WorldSettings.MaxSubmissionIterations` (default 10). Hitting the cap throws `"possible circular submission detected"` in `DEBUG` builds — usually a sign that an observer is feeding itself.

## Frame events

Separate from the per-entity events above, `World.Events` exposes lifecycle hooks for the simulation loop and for snapshot / recording loads:

| Event | Fires when |
|---|---|
| `OnVariableUpdateStarted` | At the start of every `World.Tick()`. |
| `OnFixedUpdateStarted` | At the start of each fixed-update step (zero or more times per `Tick()`, depending on catch-up). |
| `OnPostApplyInputs` | Inside each fixed step, after inputs have been applied to the global entity. |
| `OnSubmissionStarted` | Submission is about to run (end of each fixed step). |
| `OnSubmission` | Submission finished — all queued structural changes applied. |
| `OnFixedUpdateCompleted` | At the end of each fixed-update step. |
| `OnDeserializeStarted` | A snapshot or recording is about to load into the world. |
| `OnDeserializeCompleted` | A snapshot or recording has finished loading. |

Each takes an `Action` (or `Action, int priority` for ordering) and returns `IDisposable`. Dispose to unsubscribe.

```csharp
var sub = World.Events.OnSubmission(() => Debug.Log("Submission complete"));
// ...
sub.Dispose();
```
