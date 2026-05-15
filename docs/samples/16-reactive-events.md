# 16 — Reactive Events

Subscribe to entity add / remove / move events via `WorldAccessor.Events` so side-effects (logging, VFX, GameObject cleanup, stat counters) run reactively instead of by polling.

**Source:** `com.trecs.core/Samples~/Tutorials/16_ReactiveEvents/`

## What it does

A spawner adds a new `Bubble` entity every 0.3s. Each entity has a companion `GameObject` spawned reactively by `RenderableGameObjectManager` (in `Common/`). A lifetime system removes bubbles whose timer runs out. A **stats updater** subscribes to `OnAdded` and `OnRemoved` for the `Bubble` tag and bumps live-count / total-spawned / total-removed counters on a global component.

This sample focuses on the observer side: how application code subscribes to entity lifecycle events to drive its own state. The GameObject pool sitting alongside it (in `Common/`) is a real-world example of the same pattern at the framework-helper level.

## The three pieces

```csharp
public partial class GameStatsUpdater : IDisposable
{
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public GameStatsUpdater(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

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
    void OnBubbleRemoved(in Position position)
    {
        ref var stats = ref World.GlobalComponent<GameStats>().Write;
        stats.AliveCount--;
        stats.TotalRemoved++;
    }

    public void Dispose() => _disposables.Dispose();
}
```

GameObject destruction is handled separately by `RenderableGameObjectManager` (in `Common/`), which subscribes to its own `OnAdded` / `OnRemoved` for entities carrying `PrefabId` + `GameObjectId` and spawns/pools the GO behind the scenes. This sample's observer is only concerned with the stats counters.

**1. Scope** — `Events.EntitiesWithTags<T>()` picks which entities to observe. Also available: `EntitiesWithComponents<T>()`, `EntitiesWithTagsAndComponents<T>(TagSet)`, `InGroup(group)`, and `AllEntities()`.

**2. Handlers** — `OnAdded`, `OnRemoved`, `OnMoved` attach `[ForEachEntity]` methods. The source generator emits per-entity iteration and reads the requested components from the group's buffers. The handler sees one entity at a time.

**3. Lifetime** — the subscription returns an `IDisposable`. `.AddTo(_disposables)` parks it in a collection disposed when the handler class is disposed.

## Reading components in `OnRemoved`

Events fire during submission, and each entity's component data is still readable — including in `OnRemoved`, because removed entities are parked at the end of the group's backing array (past the active count) until submission completes. Declare the components you need as `in` / `ref` parameters on your `[ForEachEntity]` method and the source generator wires them up:

```csharp
[ForEachEntity]
void OnBubbleRemoved(in GameObjectId id) { /* cleanup uses `id` */ }
```

That's what makes `OnRemoved` the right place to dispose external resources keyed off entity data — the mapping from entity to GameObject, heap pointer, handle, etc. is still available at the moment of removal.

## When to reach for this

- Entities owning external resources — GameObjects, audio sources, particle systems, native handles, managed pointers — that need explicit cleanup on removal.
- Global counters, analytics, or stat tracking that should update on population changes without a system polling each frame.
- Logging or debugging: trace when specific templates appear and disappear.

For intra-ECS reactions (e.g. "when an enemy is added, spawn a spawn-VFX entity"), a normal system or init hook is often enough — reserve observers for crossing the ECS/external boundary.

## Frame events

Beyond per-entity events, `Events` exposes frame-level callbacks: `OnSubmissionStarted`, `OnSubmissionCompleted`, `OnFixedUpdateStarted`, `OnFixedUpdateCompleted`, `OnVariableUpdateStarted`, `OnVariableUpdateCompleted`, `OnInputsApplied`. See [Entity Events — Frame Events](../entity-management/entity-events.md#frame-events) for the full list.

## Concepts introduced

- **`Events.EntitiesWithTags<T>()`** — scope-builder for entity lifecycle subscriptions
- **`OnAdded` / `OnRemoved` / `OnMoved`** — observers that fire during submission
- **`[ForEachEntity]` on handlers** — source-generated per-entity iteration with component access in the method signature
- **`DisposeCollection.AddTo`** — conventional lifetime management for subscription disposables (the `DisposeCollection` type is a sample helper; supply your own `IDisposable` container in a real project)
