using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Trecs.Samples.ReactiveEvents
{
    /// <summary>
    /// Subscribes to entity add/remove events for the Bubble tag. Demonstrates
    /// the three pieces of the reactive API:
    ///
    /// 1. <c>accessor.Events.EntitiesWithTags&lt;T&gt;()</c> — select which entities
    ///    to observe.
    /// 2. <c>.OnAdded(...)</c> / <c>.OnRemoved(...)</c> — attach callbacks.
    /// 3. <c>subscription.Dispose()</c> — unsubscribe; called from the
    ///    composition root's disposables list.
    ///
    /// The observer also reads component data in the OnRemoved callback to
    /// clean up the bubble's GameObject. This is the natural home for
    /// destruction-side effects: it runs exactly once per removed entity and
    /// has access to the outgoing component values via the group's buffers.
    /// </summary>
    public class EventObserverInstaller
    {
        readonly World _world;
        readonly GameObjectRegistry _registry;
        IDisposable _subscription;

        public int AliveCount { get; private set; }
        public int TotalSpawned { get; private set; }
        public int TotalRemoved { get; private set; }

        public EventObserverInstaller(World world, GameObjectRegistry registry)
        {
            _world = world;
            _registry = registry;
        }

        public void Install()
        {
            var accessor = _world.CreateAccessor();

            _subscription = accessor
                .Events.EntitiesWithTags<SampleTags.Bubble>()
                .OnAdded(
                    (group, indices) =>
                    {
                        AliveCount += indices.Count;
                        TotalSpawned += indices.Count;
                        Debug.Log($"[Observer] OnAdded: {indices.Count} bubble(s) spawned");
                    }
                )
                .OnRemoved(
                    (group, indices, worldAccessor) =>
                    {
                        // Components are still readable at removal time — the
                        // swap-back has moved the outgoing entities to the end
                        // of the group buffer, but their values remain intact
                        // until submission finishes.
                        var gameObjectIds = worldAccessor.ComponentBuffer<GameObjectId>(group).Read;

                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            var id = gameObjectIds[i];
                            var go = _registry.Resolve(id);
                            _registry.Unregister(id);
                            Object.Destroy(go);
                        }

                        AliveCount -= indices.Count;
                        TotalRemoved += indices.Count;
                        Debug.Log($"[Observer] OnRemoved: {indices.Count} bubble(s) popped");
                    }
                );
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}
