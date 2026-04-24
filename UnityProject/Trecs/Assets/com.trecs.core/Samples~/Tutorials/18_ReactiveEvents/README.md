# 18 — Reactive Events

Shows how to subscribe to entity lifecycle events with
`WorldAccessor.Events`. Observers let you run side-effects (logging, spawning
VFX, cleaning up external resources) in response to entities being added,
removed, or moved between groups — without polling.

## What the sample does

- A `BubbleSpawnerSystem` adds a new `Bubble` entity every 0.3 s. Each entity
  has a companion `GameObject` (a small sphere) registered in the
  `GameObjectRegistry`.
- A `BubbleLifetimeSystem` removes bubbles whose lifetime has run out.
- An `EventObserverInstaller` subscribes to `OnAdded` and `OnRemoved` for the
  `Bubble` tag.
  - **OnAdded** increments counters and logs the spawn.
  - **OnRemoved** reads each outgoing entity's `GameObjectId` component,
    destroys the associated `GameObject`, and unregisters it. This is the
    reactive pattern's sweet spot: a single place that owns the cleanup side
    of entity destruction.

## Key APIs

- `accessor.Events` — entry point to the event subscription builder
  (`EntityEventsBuilder`).
- `.EntitiesWithTags<T1>()` / `.EntitiesWithTags(TagSet)` — scope the
  subscription to a specific tag combination. Also available:
  `EntitiesWithComponents<T>()`, `EntitiesWithTagsAndComponents<T>(TagSet)`,
  and `AllEntities()`.
- `.OnAdded((group, indices) => ...)` — fires during submission when entities
  are added. `indices` is a half-open `[Start, End)` range into the group's
  component buffers.
- `.OnRemoved((group, indices, world) => ...)` — three-argument overload that
  also passes a `WorldAccessor`, so the callback can read components or
  perform further queries.
- `.OnMoved((fromGroup, toGroup, indices) => ...)` — fires when entities
  transition between groups (e.g. via partition changes or tag moves). Not
  demonstrated in this sample but works analogously.
- `subscription.Dispose()` — unsubscribes all handlers. Call this from your
  world's disposables list.

## Reading components in observer callbacks

When observers fire during submission, entities in the given `EntityRange`
are still readable via their group's component buffers. Inside the callback:

```csharp
var positions = world.ComponentBuffer<Position>(group).Read;
for (int i = indices.Start; i < indices.End; i++)
{
    var pos = positions[i];
    // react to `pos`
}
```

This is how the sample's `OnRemoved` handler reaches the `GameObjectId` of
each bubble about to be destroyed.

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **ReactiveEventsCompositionRoot**.
   Drag ReactiveEventsCompositionRoot into Bootstrap's `CompositionRoot`.
3. Press Play. Bubbles spawn and pop; the Console logs each event.

Documentation: https://svermeulen.github.io/trecs/samples/18-reactive-events/
