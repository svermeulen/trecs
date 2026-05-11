using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Lightweight set gateway returned by <see cref="WorldAccessor.Set{T}"/>.
    /// Selects the set's timing mode:
    /// <list type="bullet">
    ///   <item><description><see cref="Defer"/> — queue Add / Remove / Clear for next submission. No sync, no per-call cost beyond an enqueue.</description></item>
    ///   <item><description><see cref="Read"/> — synchronous read view. Syncs outstanding writer jobs once at acquisition.</description></item>
    ///   <item><description><see cref="Write"/> — synchronous read+write view. Syncs outstanding readers and writers once at acquisition.</description></item>
    /// </list>
    /// Cache the returned view for repeated access in tight loops.
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
        /// Returns a deferred-mutation view. Add / Remove / Clear are queued and
        /// applied at the next submission. A queued Clear supersedes any queued
        /// Add / Remove for the same set regardless of call order.
        /// </summary>
        public SetDefer<T> Defer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return new SetDefer<T>(_world, _world.GetSetDeferredQueues(_setId)); }
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
