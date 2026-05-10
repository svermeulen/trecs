# Entity Events

Entity events let a service react to structural changes — that is, whenever entities are added, removed, or moved between groups. Observer callbacks fire during [submission](structural-changes.md#when-submission-happens), after the queued change has been applied.

## Subscribing

The recommended pattern is to mark your event handler with `[ForEachEntity]` so the source generator emits the per-entity iteration with the right component access:

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

Components read inside `OnRemoved` are still valid, even though the callback is triggered _after_ removal.  This is possible because removed entities are parked at the end of the backing array (past the active count), so the buffers haven't been cleared yet. This also means that the removed components are contiguous in memory and therefore cache-friendly for any cleanup.

## Event types

| Event | Trigger |
|-------|---------|
| `OnAdded` | Entities added to a matching group |
| `OnRemoved` | Entities removed from a matching group |
| `OnMoved` | Entities moved from one group to another |

## Aspects in event callbacks

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

    public void Dispose() => _disposables.Dispose();

    partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
}
```

## Disposing subscriptions

Subscriptions returned by `EntitiesWithTags` / `EntitiesWithComponents` / `InGroup` / `AllEntities` all implement `IDisposable`. Dispose the subscription to unregister the handler.

```csharp
var sub = World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .OnRemoved(OnBulletRemoved);
// ...
sub.Dispose();
```

`OnSubmission`, `OnFixedUpdateStarted`, etc. return a plain `IDisposable` for the same purpose.

Trecs doesn't ship a `DisposeCollection` type. The samples define a small helper, but a `List<IDisposable>` walked in `Dispose()` works fine too.

## Scoping events

```csharp
World.Events.EntitiesWithTags<GameTags.Player>()
World.Events.EntitiesWithTags<GameTags.Player, GameTags.Active>()
World.Events.EntitiesWithComponents<Health>()    // groups whose template declares this component
World.Events.InGroup(group)
World.Events.AllEntities()
```

Call `WithPriority(int)` before `OnAdded` / `OnRemoved` / `OnMoved` to control firing order across observers on the same group:

```csharp
World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .WithPriority(10)
    .OnRemoved(OnBulletRemoved);
```

## Cascading structural changes from callbacks

A callback can itself queue structural changes — e.g. an `OnRemoved` handler that removes a follower, or an `OnAdded` handler that spawns a child. Trecs cascades **iteratively, not recursively**:

- Each submission iteration snapshots the queued ops, applies them, and fires the matching observers. New ops queued from callbacks are picked up in the *next* iteration of the same `SubmitEntities()` call.
- The cascade continues until the queues drain, bounded by `WorldSettings.MaxSubmissionIterations` (default 10). Hitting the cap throws `"possible circular submission detected"` in `DEBUG` builds — usually a sign that an observer is feeding itself.
- Order is deterministic across iterations. Native (Burst-queued) ops are sorted at the boundary when [`RequireDeterministicSubmission`](structural-changes.md#deterministic-submission) is enabled.
- **Don't call `world.SubmitEntities()` from inside a callback** — the re-entrancy guard asserts. Just queue the change and let the running submission pick it up.

The [strict-accessor-during-Fixed-execute rule](../advanced/accessor-roles.md#strict-accessor-during-fixed-execute-rule) does **not** apply inside observer callbacks: submission runs *between* system executes, so a service can use its own `AccessorRole.Fixed` accessor from the callback. The [pointer cleanup sample](../samples/10-pointers.md) relies on this.

## Frame events

Lifecycle hooks for the simulation loop:

```csharp
World.Events.OnFixedUpdateStarted(() => { /* fixed phase begins */ });
World.Events.OnPostApplyInputs(() =>     { /* inputs applied — inside the fixed step, before systems */ });
World.Events.OnSubmissionStarted(() =>   { /* submission about to run */ });
World.Events.OnSubmission(() =>          { /* submission finished — all changes applied */ });
World.Events.OnFixedUpdateCompleted(() => { /* all fixed steps complete */ });
World.Events.OnVariableUpdateStarted(() => { /* variable phase begins */ });
```

Each returns `IDisposable`; call `.Dispose()` to unsubscribe. Each method also has a `(Action, int priority)` overload for ordering. `OnDeserializeCompleted` is also available for reacting to a snapshot/recording load.
