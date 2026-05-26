using System;

namespace Trecs.Samples.ReactiveEvents
{
    /// <summary>
    /// Subscribes to entity add/remove events for the Bubble tag. Demonstrates
    /// the three pieces of the reactive API:
    ///
    /// 1. <c>accessor.Events.EntitiesWithTags&lt;T&gt;()</c> — select which entities
    ///    to observe.
    /// 2. <c>.OnAdded(...)</c> / <c>.OnRemoved(...)</c> — attach <c>[ForEachEntity]</c>
    ///    method handlers. The source generator emits per-entity iteration and
    ///    reads the requested components from the group's buffers. Component
    ///    data is available in both callbacks — including <c>OnRemoved</c>,
    ///    since removed entities are parked at the end of the backing array
    ///    (past the active count) until submission finishes.
    /// 3. <c>subscription.Dispose()</c> — unsubscribes; called from the
    ///    composition root's disposables list.
    /// </summary>
    public partial class GameStatsUpdater : IDisposable
    {
        readonly DisposeCollection _disposables = new();

        public GameStatsUpdater(World world)
        {
            World = world.CreateAccessor(AccessorRole.Fixed);

            World
                .Events.EntitiesWithTags<SampleTags.Bubble>()
                .OnAdded(OnBubbleAdded)
                .OnRemoved(OnBubbleRemoved)
                .AddTo(_disposables);
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
}
