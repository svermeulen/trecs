using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A live entity reference bound to a <see cref="WorldAccessor"/>. Provides
    /// component access via <see cref="Get{T}"/> and structural / set / input
    /// operations on the bound entity without paying a handle-to-index lookup
    /// per call.
    /// </summary>
    public readonly ref struct EntityAccessor
    {
        readonly WorldAccessor _world;
        readonly EntityIndex _entityIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EntityAccessor(WorldAccessor world, EntityIndex entityIndex)
        {
            _world = world;
            _entityIndex = entityIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EntityAccessor(WorldAccessor world, EntityHandle entityHandle)
        {
            _world = world;
            _entityIndex = entityHandle.ToIndex(world);
        }

        /// <summary>
        /// Returns a <see cref="ComponentAccessor{T}"/> for lazy read/write access to the given component.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ComponentAccessor<T> Get<T>()
            where T : unmanaged, IEntityComponent
        {
            return new ComponentAccessor<T>(_world, _entityIndex);
        }

        /// <summary>
        /// Tries to get a <see cref="ComponentAccessor{T}"/> for this entity.
        /// Returns false if the entity does not have the component.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet<T>(out ComponentAccessor<T> componentRef)
            where T : unmanaged, IEntityComponent
        {
            return _world.TryComponent(_entityIndex, out componentRef);
        }

        internal EntityIndex EntityIndex => _entityIndex;

        /// <summary>
        /// The stable entity identifier. Performs a reverse lookup.
        /// </summary>
        public EntityHandle Handle
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entityIndex.ToHandle(_world);
        }

        /// <summary>
        /// The <see cref="WorldAccessor"/> this reference is bound to.
        /// </summary>
        public WorldAccessor World => _world;

        // ── Structural operations ─────────────────────────────────────

        /// <summary>
        /// Schedules removal of this entity. Applied at the next submission.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove() => _world.RemoveEntity(_entityIndex);

        /// <summary>
        /// Schedules moving this entity to the group identified by the given tags.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveTo(TagSet tags) => _world.MoveTo(_entityIndex, tags);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveTo<T1>()
            where T1 : struct, ITag => _world.MoveTo<T1>(_entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveTo<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => _world.MoveTo<T1, T2>(_entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveTo<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => _world.MoveTo<T1, T2, T3>(_entityIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MoveTo<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => _world.MoveTo<T1, T2, T3, T4>(_entityIndex);

        // ── Set operations ────────────────────────────────────────────

        /// <summary>
        /// Adds this entity to the given set immediately.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddToSet<TSet>(SetWrite<TSet> set)
            where TSet : struct, IEntitySet => set.Add(_entityIndex);

        /// <summary>
        /// Removes this entity from the given set immediately.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveFromSet<TSet>(SetWrite<TSet> set)
            where TSet : struct, IEntitySet => set.Remove(_entityIndex);

        /// <summary>
        /// Returns true if this entity is a member of the given set.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ExistsInSet<TSet>(SetRead<TSet> set)
            where TSet : struct, IEntitySet => set.Exists(_entityIndex);

        // ── Input ─────────────────────────────────────────────────────

        /// <summary>
        /// Queues an input value for this entity to be applied at the start of the
        /// next fixed step.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddInput<T>(in T value)
            where T : unmanaged, IEntityComponent => _world.AddInput(_entityIndex, value);
    }
}
