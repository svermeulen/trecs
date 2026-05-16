using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Lightweight set gateway returned by <see cref="WorldAccessor.Set{T}"/>.
    /// Selects the set's timing mode:
    /// <list type="bullet">
    ///   <item><description><see cref="DeferredAdd(EntityHandle)"/> / <see cref="DeferredRemove(EntityHandle)"/> / <see cref="DeferredClear"/> — queue for next submission. No sync, no per-call cost beyond an enqueue.</description></item>
    ///   <item><description><see cref="Read"/> — synchronous read view. Syncs outstanding writer jobs once at acquisition.</description></item>
    ///   <item><description><see cref="Write"/> — synchronous read+write view. Syncs outstanding readers and writers once at acquisition.</description></item>
    /// </list>
    /// Cache the returned <c>.Read</c> / <c>.Write</c> view for repeated access in tight loops.
    /// <c>Deferred*</c> methods sit directly on the accessor — there's no state to cache between calls.
    /// </summary>
    public readonly ref struct SetAccessor<T>
        where T : struct, IEntitySet
    {
        readonly WorldAccessor _world;
        readonly SetId _setId;

        internal SetAccessor(WorldAccessor world)
        {
            _world = world;
            _setId = EntitySet<T>.Value.Id;
        }

        /// <summary>
        /// Queue an Add for the next submission. No sync — the per-set deferred
        /// queues are independent of any outstanding job state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredAdd(EntityIndex entityIndex)
        {
            // Slot 0 is the main-thread writer slot — see NativeSetDeferredQueues
            // for the full slot-layout invariant (worker threads always get a
            // non-zero index, so no concurrent write to slot 0 is possible).
            _world.GetSetDeferredQueues(_setId).AddQueue.GetBag(0).Enqueue(entityIndex);
        }

        /// <summary>
        /// Queue an Add for the next submission. See
        /// <see cref="DeferredAdd(EntityIndex)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredAdd(EntityHandle entityHandle)
        {
            DeferredAdd(entityHandle.ToIndex(_world));
        }

        /// <summary>
        /// Queue a Remove for the next submission. No sync.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredRemove(EntityIndex entityIndex)
        {
            // Slot 0 is the main-thread writer slot — see NativeSetDeferredQueues
            // for the full slot-layout invariant.
            _world.GetSetDeferredQueues(_setId).RemoveQueue.GetBag(0).Enqueue(entityIndex);
        }

        /// <summary>
        /// Queue a Remove for the next submission. See
        /// <see cref="DeferredRemove(EntityIndex)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredRemove(EntityHandle entityHandle)
        {
            DeferredRemove(entityHandle.ToIndex(_world));
        }

        /// <summary>
        /// Queue a Clear for the next submission. At submission time, a queued
        /// clear supersedes any queued <see cref="DeferredAdd(EntityIndex)"/> /
        /// <see cref="DeferredRemove(EntityIndex)"/> for the same set, regardless
        /// of call order — analogous to how a queued remove supersedes a queued
        /// move on an entity. Use <see cref="SetWrite{T}.Clear"/> if you need
        /// the clear to take effect within the current frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeferredClear()
        {
            _world.GetSetDeferredQueues(_setId).RequestClear();
        }

        /// <summary>
        /// Returns a synchronous read-only view after syncing outstanding writer jobs.
        /// Cache the result for repeated access in a tight loop.
        /// </summary>
        public SetRead<T> Read
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                _world.SyncSetForRead(_setId);
                return new SetRead<T>(_world, _world.GetSetCollection(_setId));
            }
        }

        /// <summary>
        /// Returns a synchronous read+write view after syncing all outstanding jobs.
        /// Cache the result for repeated access in a tight loop.
        /// </summary>
        public SetWrite<T> Write
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                _world.SyncSetForWrite(_setId);
                return new SetWrite<T>(_world, _setId, _world.GetSetCollection(_setId));
            }
        }
    }
}
