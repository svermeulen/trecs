# Entity Events

Entity events let a service react to structural changes — when entities are added to, removed from, or moved between groups. Observer callbacks fire during [submission](structural-changes.md#when-submission-happens), after the queued change has been applied.

## Subscribing to Events

The recommended pattern is to mark your handler with `[ForEachEntity]` so the source generator emits the per-entity iteration with the right component access:

```csharp
public partial class RemoveCleanupHandler : IDisposable
{
    readonly DisposeCollection _disposables = new(); // sample helper — see note below

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

    public void Dispose() => _disposables.Dispose();
}
```

Components read inside `OnRemoved` are still valid — removed entities are parked at the end of the backing array (past the active count) until submission finishes, so the buffers haven't been recycled yet.

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

The chain returned by `EntitiesWithTags` / `EntitiesWithComponents` / `InGroup` / `AllEntities` is an `EntityEventsSubscription` (which implements `IDisposable`). Disposing it unregisters every observer on the chain (`OnAdded`, `OnRemoved`, `OnMoved`).

```csharp
var sub = World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .OnRemoved(OnBulletRemoved);
// ...
sub.Dispose();  // unsubscribes
```

`OnSubmission`, `OnFixedUpdateStarted`, etc. return a plain `IDisposable` for the same purpose.

Trecs doesn't ship a `DisposeCollection` type. The samples define a small helper, but a plain `List<IDisposable>` you walk in `Dispose()` works fine too.

## Scoping Events

```csharp
// By a single tag
World.Events.EntitiesWithTags<GameTags.Player>()

// By multiple tags
World.Events.EntitiesWithTags<GameTags.Player, GameTags.Active>()

// By component presence (groups whose template declares this component)
World.Events.EntitiesWithComponents<Health>()

// By a specific group
World.Events.InGroup(group)

// All entities
World.Events.AllEntities()
```

Call `WithPriority(int)` before `OnAdded` / `OnRemoved` / `OnMoved` to control firing order across observers on the same group:

```csharp
World.Events
    .EntitiesWithTags<GameTags.Bullet>()
    .WithPriority(10)
    .OnRemoved(OnBulletRemoved);
```

## Cascading Structural Changes from Callbacks

A callback can itself queue structural changes — e.g. an `OnRemoved` handler that removes a follower, or an `OnAdded` handler that spawns a child. Trecs cascades **iteratively, not recursively**:

- Each submission iteration snapshots the queued ops, applies them, and fires the matching observers. New ops queued from those callbacks land in a fresh buffer and are picked up in the *next* iteration of the same `SubmitEntities()` call.
- The cascade continues until the queues drain, bounded by `WorldSettings.MaxSubmissionIterations` (default 10). Hitting the cap throws `"possible circular submission detected"` in `DEBUG` builds — usually a sign that an observer is feeding itself.
- Order is deterministic across iterations: each iteration walks groups and observers in the same fixed order, and native (Burst-queued) ops are sorted at the boundary when [`RequireDeterministicSubmission`](structural-changes.md#deterministic-submission) is enabled.
- **Don't call `world.SubmitEntities()` from inside a callback** — the re-entrancy guard asserts. Just queue the change and let the running submission pick it up.

The [strict-accessor-during-Fixed-execute rule](../advanced/accessor-roles.md#strict-accessor-during-fixed-execute-rule) does **not** apply inside observer callbacks: submission runs *between* system executes, so a service can use its own `AccessorRole.Fixed` accessor from the callback. The [pointer cleanup sample](../samples/10-pointers.md) relies on this.

## Frame Events

Lifecycle hooks for the simulation loop:

```csharp
World.Events.OnFixedUpdateStarted(() => { /* fixed phase begins */ });
World.Events.OnPostApplyInputs(() =>     { /* inputs applied — inside the fixed step, before systems */ });
World.Events.OnSubmissionStarted(() =>   { /* submission about to run */ });
World.Events.OnSubmission(() =>          { /* submission finished — all changes applied */ });
World.Events.OnFixedUpdateCompleted(() => { /* all fixed steps complete */ });
World.Events.OnVariableUpdateStarted(() => { /* variable phase begins */ });
```

Each returns `IDisposable`; call `.Dispose()` to unsubscribe. Each method also has a `(Action, int priority)` overload for ordering. There's also `OnDeserializeCompleted` for reacting to a snapshot/recording load.

