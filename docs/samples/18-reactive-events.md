# 18 — Reactive Events

Subscribing to entity add / remove / move events with `WorldAccessor.Events` so side-effects (logging, VFX, GameObject cleanup, stat counters) run reactively instead of by polling.

**Source:** `Samples/18_ReactiveEvents/`

## What it does

A spawner adds a new `Bubble` entity every 0.3s. Each entity has a companion `GameObject` registered in the `GameObjectRegistry`. A lifetime system removes bubbles whose timer runs out. A **stats updater** object subscribes to `OnAdded` and `OnRemoved` for the `Bubble` tag:

- **OnAdded** increments live-count and total-spawned counters on a global component.
- **OnRemoved** destroys the associated `GameObject`, unregisters it from the registry, and updates the counters.

This is the canonical pattern for managing external, non-ECS resources tied to entities — one place that owns the cleanup side of destruction.

## The three pieces

```csharp
public partial class GameStatsUpdater : IDisposable
{
    readonly GameObjectRegistry _registry;
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public GameStatsUpdater(World world, GameObjectRegistry registry)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);
        _registry = registry;

        World
            .Events.EntitiesWithTags<SampleTags.Bubble>()   // 1. scope
            .OnAdded(OnBubbleAdded)                         // 2. handlers
            .OnRemoved(OnBubbleRemoved)
            .AddTo(_disposables);                           // 3. lifetime
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnBubbleAdded(in Position position)
    {
        ref var stats = ref World.GlobalComponent<GameStats>().Write;
        stats.AliveCount++;
        stats.TotalSpawned++;
    }

    [ForEachEntity]
    void OnBubbleRemoved(in GameObjectId id)
    {
        var go = _registry.Resolve(id);
        _registry.Unregister(id);
        Object.Destroy(go);

        ref var stats = ref World.GlobalComponent<GameStats>().Write;
        stats.AliveCount--;
        stats.TotalRemoved++;
    }

    public void Dispose() => _disposables.Dispose();
}
```

**1. Scope** — `Events.EntitiesWithTags<T>()` picks which entities to observe. Also available: `EntitiesWithComponents<T>()`, `EntitiesWithTagsAndComponents<T>(TagSet)`, `InGroup(group)`, and `AllEntities()`.

**2. Handlers** — `OnAdded`, `OnRemoved`, `OnMoved` attach `[ForEachEntity]` methods. The source generator emits per-entity iteration and reads the requested components from the group's buffers. The handler body only ever sees one entity at a time.

**3. Lifetime** — the subscription returns an `IDisposable`. `.AddTo(_disposables)` parks it in a collection that's disposed when the handler class itself is disposed.

## Reading components in `OnRemoved`

Events fire during submission, and each entity's component data is still readable — including in `OnRemoved`, because removed entities are parked at the end of the group's backing array (past the active count) until submission completes. Declare whichever components you need as `in` / `ref` parameters on your `[ForEachEntity]` method and the source generator wires them up:

```csharp
[ForEachEntity]
void OnBubbleRemoved(in GameObjectId id) { /* cleanup uses `id` */ }
```

This is what makes `OnRemoved` the right place to dispose external resources keyed off entity data — the mapping from entity to GameObject, heap pointer, handle, etc. is still available at the moment of removal.

## When to reach for this

- Entities that own external resources — GameObjects, audio sources, particle systems, native handles, managed pointers — which need explicit cleanup when the entity is removed.
- Global counters, analytics, or stat tracking that should update whenever a population changes, without a system polling each frame.
- Logging or debugging: trace when specific templates appear and disappear.

For intra-ECS reactions (e.g. "when an enemy is added, spawn a spawn-VFX entity"), you can often do it in a normal system or init hook instead — reserve observers for crossing the ECS/external boundary, which is the problem they solve best.

## Frame events

Outside of per-entity events, `Events` also exposes frame-level callbacks: `OnSubmissionStarted`, `OnSubmissionCompleted`, `OnFixedUpdateStarted`, `OnFixedUpdateCompleted`, `OnVariableUpdateStarted`, `OnVariableUpdateCompleted`, `OnInputsApplied`. See [Entity Events — Frame Events](../entity-management/entity-events.md#frame-events) for the full list.

## Concepts introduced

- **`Events.EntitiesWithTags<T>()`** — scope-builder for entity lifecycle subscriptions
- **`OnAdded` / `OnRemoved` / `OnMoved`** — observers that fire during submission
- **`[ForEachEntity]` on handlers** — source-generated per-entity iteration with component access on the method signature
- **`DisposeCollection.AddTo`** — conventional lifetime management for subscription disposables (the `DisposeCollection` type is a sample helper; supply your own `IDisposable` container in a real project)
