using System.Runtime.CompilerServices;
using Trecs.Internal;

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

        public ref readonly T Read
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _world.GetComponentRead<T>(_entityIndex).Value;
        }

        public ref T Write
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _world.GetComponentWrite<T>(_entityIndex).Value;
        }

        internal EntityIndex EntityIndex => _entityIndex;
    }
}
