# Entity Events

Entity events let a service react to structural changes — entities added, removed, or moved between groups. Observer callbacks fire during [submission](structural-changes.md#when-submission-happens), after the queued change has been applied.

## Anatomy of a subscription

Build a subscription in three parts: pick the **scope** (which groups to watch), the **event** (add / remove / move), and a **handler**:

```csharp
World.Events
    .EntitiesWithTags<MyTag>()    // 1. scope
    .OnRemoved(OnEntityRemoved);  // 2. event   (3. handler is the method passed in)
```

### Scopes

A scope picks which groups the subscription watches:

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
            targetMeal.Value.Remove(World);
    }

    public void Dispose() => _disposables.Dispose();
}
```

All `[ForEachEntity]` features are supported on event handlers, including aspects:

```csharp
public partial class CleanupHandlers : IDisposable
{
    readonly RenderableGameObjectManager _goManager;
    readonly DisposeCollection _disposables = new();

    public CleanupHandlers(World world, RenderableGameObjectManager goManager)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);
        _goManager = goManager;

        World.Events
            .EntitiesWithTags<SampleTags.Prey>()
            .OnRemoved(OnPreyRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnPreyRemoved(in Prey prey)
    {
        var go = _goManager.Resolve(prey.GameObjectId);
        UnityEngine.Object.Destroy(go);
    }

    public void Dispose() => _disposables.Dispose();

    partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
}
```

Components read inside `OnRemoved` are still valid even though the callback fires *after* removal — removed entities are parked at the end of the backing array (past the active count), so the buffers haven't been cleared. Removed components are contiguous in memory, which is cache-friendly for cleanup.

## Priorities

Call `WithPriority(int)` before `OnAdded` / `OnRemoved` / `OnMoved` to control firing order across observers on the same group (higher = later; default `0`):

```csharp
World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .WithPriority(10)
    .OnRemoved(OnBulletRemoved);
```

## Disposing subscriptions

Subscription objects implement `IDisposable`. Dispose to unregister the handler.

```csharp
var sub = World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .OnRemoved(OnBulletRemoved);
// ...
sub.Dispose();
```

Trecs doesn't ship a `DisposeCollection` type for aggregating subscriptions. The samples define a small helper, but a `List<IDisposable>` walked during cleanup works fine too.

## Cascading structural changes from callbacks

A callback can itself queue structural changes — e.g. an `OnRemoved` handler that removes a follower, or an `OnAdded` handler that spawns a child. Trecs keeps processing the queue until empty or until `WorldSettings.MaxSubmissionIterations` (default 10) is reached. Hitting the cap throws `"possible circular submission detected"` in `DEBUG` builds — usually a sign that an observer is feeding itself.

## Frame events

Separate from the per-entity events, `World.Events` exposes lifecycle hooks for the simulation loop and for snapshot / recording loads. The trigger times below align with the [per-frame phase diagram](../core/systems.md#phase-diagram):

| Event | Fires when |
|---|---|
| `OnVariableUpdateStarted` | At the start of every `World.Tick()`. |
| `OnFixedUpdateStarted` | At the start of each fixed-update step (zero or more times per `Tick()`, depending on catch-up). |
| `OnInputsApplied` | Inside each fixed step, after queued `AddInput<T>` values have been written onto their target entities (typically the global entity, but any entity is valid). |
| `OnSubmissionStarted` | Submission is about to run (end of each fixed step). |
| `OnSubmissionCompleted` | Submission finished — all queued structural changes applied. |
| `OnFixedUpdateCompleted` | At the end of each fixed-update step. |
| `OnVariableUpdateCompleted` | At the end of every `World.LateTick()`, after the final submission for the frame. |
| `OnDeserializeStarted` | A snapshot or recording is about to load into the world. |
| `OnDeserializeCompleted` | A snapshot or recording has finished loading. |

Each takes an `Action` (or `Action, int priority` for ordering) and returns `IDisposable`. Dispose to unsubscribe.

```csharp
var sub = World.Events.OnSubmissionCompleted(() => Debug.Log("Submission complete"));
// ...
sub.Dispose();
```
