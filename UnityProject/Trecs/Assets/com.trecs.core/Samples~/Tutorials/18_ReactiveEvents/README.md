# 18 — Reactive Events

Shows how to subscribe to entity lifecycle events with
`WorldAccessor.Events`. Observers let you run side-effects (logging, spawning
VFX, cleaning up external resources) in response to entities being added,
removed, or moved between groups — without polling.

## What the sample does

- A `BubbleSpawnerSystem` adds a new `Bubble` entity every 0.3 s.
  `RenderableGameObjectManager` reactively spawns the companion `GameObject`
  (a small sphere) when it observes the new entity, so the spawn system
  itself does not touch Unity GameObjects.
- A `BubbleLifetimeSystem` removes bubbles whose lifetime has run out.
- A `GameStatsUpdater` subscribes to `OnAdded` and `OnRemoved` for the
  `Bubble` tag and maintains an `AliveCount`/`TotalSpawned`/`TotalRemoved`
  global stats component (rendered by `TextDisplaySystem`).
  - **OnAdded** bumps `AliveCount` and `TotalSpawned`.
  - **OnRemoved** bumps `TotalRemoved` and decrements `AliveCount`.
  GameObject cleanup happens in the manager's own `OnRemoved` subscription
  — observers compose cleanly.

  Both handlers are `[ForEachEntity]` methods — the source generator emits
  the per-entity iteration, so the handler body only has to deal with one
  entity at a time.

## Key APIs

- `accessor.Events` — entry point to the event subscription builder
  (`EntityEventsBuilder`).
- `.EntitiesWithTags<T1>()` / `.EntitiesWithTags(TagSet)` — scope the
  subscription to a specific tag combination. Also available:
  `EntitiesWithComponents<T>()`, `EntitiesWithTagsAndComponents<T>(TagSet)`,
  and `AllEntities()`.
- `.OnAdded(handler)` / `.OnRemoved(handler)` — fire during submission when
  entities are added or removed. The recommended handler shape is a
  `[ForEachEntity]` method on the owning class: declare whichever components
  you need as `in`/`ref` parameters, and the source generator emits the
  per-entity iteration loop that reads them from the group's buffers.
- `.OnMoved(handler)` — fires when entities transition between groups (e.g.
  via partition changes or tag moves). Not demonstrated in this sample but
  works analogously.
- `subscription.Dispose()` — unsubscribes all handlers. Call this from your
  world's disposables list.

## Reading components in observer callbacks

Observers fire during submission, and each entity's component data is still
readable — including in `OnRemoved`, because removed entities are parked at
the end of the group's backing array until submission finishes. Declare the
components you want on a `[ForEachEntity]` method and pass the method group
to the subscription:

```csharp
World
    .Events.EntitiesWithTags<SampleTags.Bubble>()
    .OnRemoved(OnBubbleRemoved)
    .AddTo(_disposables);

[ForEachEntity]
void OnBubbleRemoved(in Position position) { /* react to the removal */ }
```

This is how the sample's `OnRemoved` handler reaches each bubble's
`Position` (or any other component on the entity) at the moment of
removal.

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **ReactiveEventsCompositionRoot**.
   Drag ReactiveEventsCompositionRoot into Bootstrap's `CompositionRoot`.
3. Press Play. Bubbles spawn and pop; the Console logs each event.

Documentation: https://svermeulen.github.io/trecs/samples/18-reactive-events/
