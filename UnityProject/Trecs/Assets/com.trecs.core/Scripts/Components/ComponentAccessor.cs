using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Unified single-entity component access wrapper with lazy read/write sync.
    /// Access <see cref="Read"/> or <see cref="Write"/> to trigger sync and get a direct ref.
    /// </summary>
    public readonly ref struct ComponentAccessor<T>
        where T : unmanaged, IEntityComponent
    {
        readonly WorldAccessor _world;
        readonly EntityIndex _entityIndex;

        internal ComponentAccessor(WorldAccessor world, EntityIndex entityIndex)
        {
            _world = world;
            _entityIndex = entityIndex;
        }

        /// <summary>
        /// Triggers a read sync and returns a <c>ref readonly</c> to the component value.
        /// Use this when you only need to inspect the component — it does not mark the
        /// component dirty for writers.
        /// </summary>
        public ref readonly T Read
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _world.GetComponentRead<T>(_entityIndex).Value;
        }

        /// <summary>
        /// Triggers a write sync and returns a mutable <c>ref</c> to the component value.
        /// Mutating through this ref updates the component in place; prefer <see cref="Read"/>
        /// when you don't intend to modify it.
        /// </summary>
        public ref T Write
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _world.GetComponentWrite<T>(_entityIndex).Value;
        }

        internal EntityIndex EntityIndex => _entityIndex;
    }
}
