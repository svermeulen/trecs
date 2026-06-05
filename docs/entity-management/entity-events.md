# Entity Events

Entity events let a service react to structural changes — entities added, removed, or moved between partitions. Observer callbacks fire during [submission](structural-changes.md#when-submission-happens), after the queued change has been applied.

## Anatomy of a subscription

Build a subscription in three parts: pick the **scope** (which entities to watch), the **event** (add / remove / move), and a **handler**:

```csharp
World.Events
    .EntitiesWithTags<MyTag>()    // 1. scope
    .OnRemoved(OnEntityRemoved);  // 2. event   (3. handler)
```

### Scopes

A scope picks which entities the subscription watches:

| Method | Matches |
|---|---|
| `EntitiesWithTags<...>()` | Entities whose tag set includes all of the given tags |
| `EntitiesWithComponents<T...>()` | Entities whose template declares all of the given components (up to 4 type parameters) |
| `EntitiesWithTagsAndComponents<T...>(TagSet)` | Entities matching both the given tags and the given components |
| `EntitiesInGroup(GroupIndex)` | Entities in one specific [group](../advanced/groups-and-tagsets.md) |
| `AllEntities()` | Every entity |

### Events

| Event | Trigger |
|-------|---------|
| `OnAdded` | Entities that match the scope after a structural change |
| `OnRemoved` | Entities removed from matching scope |
| `OnMoved` | Entities that changed partition (tag combination) |

### Handlers

The recommended pattern is to use a `[ForEachEntity]` method as the event handler. In a system, subscribe in `OnReady` and dispose in `OnShutdown`:

```csharp
public partial class FishDeathSystem : ISystem
{
    IDisposable _onFishRemoved;

    partial void OnReady()
    {
        _onFishRemoved = World.Events
            .EntitiesWithTags<FrenzyTags.Fish>()
            .OnRemoved(OnFishRemoved);
    }

    partial void OnShutdown() => _onFishRemoved?.Dispose();

    [ForEachEntity]
    void OnFishRemoved(in TargetMeal targetMeal)
    {
        if (targetMeal.Value.Exists(World))
            targetMeal.Value.Remove(World);
    }

    public void Execute() { }
}
```

See [OnReady](../core/systems.md#onready-hook) and [OnShutdown](../core/systems.md#onshutdown-hook) for system lifecycle details.

The same pattern works outside systems — any class that has access to a `WorldAccessor` can subscribe. Use a `DisposeCollection` when managing multiple subscriptions, and dispose them from the [`OnShutdown` frame event](#frame-events) so the handler is still subscribed for the final `OnRemoved` pass during `World.Dispose()` (see the warning below):

```csharp
public partial class RemoveCleanupHandler
{
    readonly DisposeCollection _disposables = new();

    public RemoveCleanupHandler(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

        World.Events
            .EntitiesWithTags<FrenzyTags.Fish>()
            .OnRemoved(OnFishRemoved)
            .AddTo(_disposables);

        // Tear down our subscriptions when the world shuts down, after the
        // final OnRemoved pass has run.
        World.Events.OnShutdown(() => _disposables.Dispose()).AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnFishRemoved(in TargetMeal targetMeal)
    {
        if (targetMeal.Value.Exists(World))
            targetMeal.Value.Remove(World);
    }
}
```

!!! warning "Dispose subscriptions in `OnShutdown`, not before `World.Dispose()`"
    `World.Dispose()` runs `RemoveAllEntities` (firing a final `OnRemoved` for every
    entity) **before** it fires the [`OnShutdown` event](#frame-events). If you dispose
    your subscriptions *before* calling `World.Dispose()` — for example from a
    composition root's disposables list ordered `{ handler.Dispose, world.Dispose }` —
    the handler is already unsubscribed when that final `OnRemoved` fires, so any
    cleanup it performs is silently skipped. This is invisible when the handler only
    touches ECS state (it's all torn down anyway), but it's a real leak when `OnRemoved`
    frees resources *outside* the world, such as destroying GameObjects. Registering
    disposal through `World.Events.OnShutdown(...)` avoids this: `OnShutdown` fires after
    the final `OnRemoved` pass but while the events system is still alive.

All `[ForEachEntity]` features are supported on event handlers, including aspects:

```csharp
public partial class CleanupHandlers
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

        World.Events.OnShutdown(() => _disposables.Dispose()).AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnPreyRemoved(in Prey prey)
    {
        // Destroying the GameObject here is exactly why disposal must run from
        // OnShutdown: if this handler were disposed before World.Dispose(), the
        // GameObjects for the final batch of prey would never be destroyed.
        var go = _goManager.Resolve(prey.GameObjectId);
        UnityEngine.Object.Destroy(go);
    }

    partial struct Prey : IAspect, IRead<GameObjectId, ApproachingPredator> { }
}
```

Components read inside `OnRemoved` are still valid even though the callback fires *after* removal — removed entities are parked at the end of the backing array (past the active count), so the buffers haven't been cleared. Removed components are contiguous in memory, which is cache-friendly for cleanup.

Beyond `[ForEachEntity]` parameters, you can also read removed entity data dynamically via `EntityIndex.Component<T>()`. This is useful when the set of components you need isn't known at compile time:

```csharp
World.Events
    .EntitiesWithTags<MyTag>()
    .OnRemoved((group, indices) =>
    {
        for (int i = indices.Start; i < indices.End; i++)
        {
            var entityIndex = new EntityIndex(i, group);
            var health = entityIndex.Component<Health>(World).Read;
            // ...
        }
    });
```

### The `OnRemoved` contract

Inside an `OnRemoved` callback, for each entity being removed:

- **Component data is still readable** (as above) — both `[ForEachEntity]` parameters and dynamic `EntityIndex.Component<T>()` reads see the pre-removal values.
- **`EntityHandle.Exists()` returns `false`.** It reflects liveness, not data accessibility. Guard cross-entity cleanup with `Exists()` as usual to avoid operating on already-removed entities.
- **Set membership** is still visible for removals that empty the group — a single entity, or a whole-group [`RemoveEntitiesWithTags` / `RemoveAllEntitiesInGroup` / `RemoveAllEntities`](structural-changes.md#removing-entities-in-bulk). The entity is removed from its [sets](sets.md) only *after* the callback, mirroring component-data accessibility. For partial batch removals that leave other entities in the same group, departed entities are removed from their sets *before* the callback (the survivor swap-back rekeys set slots). After the submission completes, removed entities are gone from every set in all cases.

This contract is **identical** across every removal path: a single `RemoveEntity`, a bulk `RemoveEntitiesWithTags` / `RemoveAllEntitiesInGroup`, and the full `RemoveAllEntities` (including the automatic pass during `World.Dispose()`).

## Priorities

Call `WithPriority(int)` before `OnAdded` / `OnRemoved` / `OnMoved` to control firing order across observers on the same scope (higher = later; default `0`):

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

The `DisposeCollection` used in the examples above is a small helper defined in the samples — Trecs core doesn't ship it. A `List<IDisposable>` walked in `Dispose()` works just as well.

## Lifecycle guarantee

Every entity that is successfully added and submitted fires exactly one `OnAdded`, and is guaranteed a matching `OnRemoved` no later than `World.Dispose()`. Two exceptions:

- the **global singleton entity** is never removed (its lifetime is the world's), so it never fires `OnRemoved`; and
- an `AddEntity` rejected by the **shutdown guard** — a structural add attempted after `World.Dispose()` has begun — is never added and fires neither event (it throws in debug builds and is dropped in release; see [world setup](../core/world-setup.md#disposal)).

This holds even when an add and a bulk removal of the same group land in the *same* submit: the entity is added (firing `OnAdded`) and then removed by the bulk removal (firing `OnRemoved`) — both events fire, never just one. See [submission phase order](structural-changes.md#submission-phase-order) for why.

## Cascading structural changes from callbacks

A callback can itself queue structural changes — e.g. an `OnRemoved` handler that removes a follower, or an `OnAdded` handler that spawns a child. Trecs keeps processing the queue until empty or until `WorldSettings.MaxSubmissionIterations` (default 10) is reached. Hitting the cap throws `"possible circular submission detected"` in debug/editor builds.

## Frame events

Separate from the per-entity events, `World.Events` exposes lifecycle hooks for the simulation loop and for snapshot / recording loads. The trigger times below align with the [per-frame phase diagram](../core/systems.md#phase-diagram):

| Event | Fires when |
|---|---|
| `OnVariableUpdateStarted` | At the start of every `World.Tick()`, after `VariableDeltaTime` has been updated. |
| `OnFixedUpdateStarted` | At the start of each fixed-update step (zero or more times per `Tick()`, depending on catch-up). |
| `OnInputsApplied` | Inside each fixed step, after queued `AddInput<T>` values have been written onto their target entities (typically the global entity, but any entity is valid). |
| `OnSubmissionStarted` | Submission is about to run. Fires at the start of every `Submit()` call — at the end of each fixed step, at the end of `World.LateTick()`, and on any manual `World.Submit()` (on the `World` class). |
| `OnSubmissionCompleted` | Submission finished — all queued structural changes applied. Only fires when at least one structural change was processed. |
| `OnFixedUpdateCompleted` | At the end of each fixed-update step. |
| `OnVariableUpdateCompleted` | At the end of every `World.LateTick()`, after the final submission for the frame. |
| `OnDeserializeStarted` | A snapshot or recording is about to load into the world. |
| `OnDeserializeCompleted` | A snapshot or recording has finished loading. |
| `OnFixedPauseChanged` | `WorldAccessor.FixedIsPaused` just toggled. Callback receives the new value (`Action<bool>`). |
| `OnShutdown` | During `World.Dispose()`, after `RemoveAllEntities` and system `OnShutdown` hooks have run but before infrastructure teardown. Use this to dispose event subscriptions from non-system code. |

Each takes an `Action` (or `Action, int priority` for ordering) and returns `IDisposable`. Dispose to unsubscribe.

```csharp
var sub = World.Events.OnSubmissionCompleted(() => Debug.Log("Submission complete"));
// ...
sub.Dispose();
```
