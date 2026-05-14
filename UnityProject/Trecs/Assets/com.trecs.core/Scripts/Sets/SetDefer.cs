using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Deferred-mutation set view returned by <see cref="SetAccessor{T}.Defer"/>.
    /// Operations are queued and applied at the next submission. No sync is performed —
    /// the per-set deferred queues are independent of any outstanding job state.
    /// <para>
    /// At submission time, a queued <see cref="Clear"/> supersedes any queued
    /// <see cref="Add(EntityIndex)"/> / <see cref="Remove(EntityIndex)"/> for the
    /// same set, regardless of call order — analogous to how a queued remove
    /// supersedes a queued move on an entity. Use <see cref="SetWrite{T}.Clear"/>
    /// if you need the clear to take effect within the current frame.
    /// </para>
    /// </summary>
    public readonly ref struct SetDefer<T>
        where T : struct, IEntitySet
    {
        readonly WorldAccessor _world;
        readonly NativeSetDeferredQueues _queues;

        internal SetDefer(WorldAccessor world, in NativeSetDeferredQueues queues)
        {
            _world = world;
            _queues = queues;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(EntityIndex entityIndex)
        {
            _queues.AddQueue.GetBag(0).Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(EntityHandle entityHandle)
        {
            Add(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(EntityIndex entityIndex)
        {
            _queues.RemoveQueue.GetBag(0).Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(EntityHandle entityHandle)
        {
            Remove(entityHandle.ToIndex(_world));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _queues.RequestClear();
        }
    }
}
