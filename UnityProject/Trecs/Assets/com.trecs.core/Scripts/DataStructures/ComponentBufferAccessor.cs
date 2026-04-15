using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Unified buffer access wrapper with lazy read/write sync.
    /// Access <see cref="Read"/> or <see cref="Write"/> to trigger sync and get the concrete buffer.
    /// </summary>
    public readonly ref struct ComponentBufferAccessor<T>
        where T : unmanaged, IEntityComponent
    {
        readonly WorldAccessor _world;
        readonly Group _group;

        internal ComponentBufferAccessor(WorldAccessor ecs, Group group)
        {
            _world = ecs;
            _group = group;
        }

        public NativeComponentBufferRead<T> Read
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _world.GetBufferRead<T>(_group);
        }

        public NativeComponentBufferWrite<T> Write
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _world.GetBufferWrite<T>(_group);
        }

        public Group Group => _group;
    }
}
