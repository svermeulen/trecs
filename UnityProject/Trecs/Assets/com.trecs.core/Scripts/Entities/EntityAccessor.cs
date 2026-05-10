using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A live entity reference that provides component access via <see cref="Get{T}"/>.
    /// Constructed from an <see cref="EntityIndex"/> or <see cref="EntityHandle"/> plus an <see cref="WorldAccessor"/>.
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

        /// <summary>
        /// The transient buffer index for this entity.
        /// </summary>
        public EntityIndex EntityIndex => _entityIndex;

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
    }
}
