using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Lightweight set handle returned by <see cref="WorldAccessor.Set{T}"/>.
    ///
    /// Use <see cref="Read"/> or <see cref="Write"/> to get a synced view for
    /// batch operations in tight loops (sync + lookup happen once, not per-call).
    ///
    /// For deferred mutations use <see cref="WorldAccessor.SetAdd{T}"/>
    /// and <see cref="WorldAccessor.SetRemove{T}"/> directly.
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
        /// Returns a read-only view after syncing outstanding writer jobs.
        /// Cache the result for repeated access in a tight loop.
        /// </summary>
        public SetRead<T> Read
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                _world.SyncSetForRead(_setId);
                return new SetRead<T>(_world, _world.GetSetCollection(_setId)._entriesPerGroup);
            }
        }

        /// <summary>
        /// Returns a read+write view after syncing all outstanding jobs.
        /// Cache the result for repeated access in a tight loop.
        /// </summary>
        public SetWrite<T> Write
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                _world.SyncSetForWrite(_setId);
                return new SetWrite<T>(
                    _world,
                    _setId,
                    _world.GetSetCollection(_setId)._entriesPerGroup
                );
            }
        }
    }
}
