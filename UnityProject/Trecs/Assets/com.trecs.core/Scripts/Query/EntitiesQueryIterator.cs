using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Flat entity iterator that yields <see cref="EntityAccessor"/> values for all entities
    /// matching a query. Each accessor is bound to the world and the entity's transient index,
    /// so structural / set / input operations on the accessor cost no extra lookup.
    /// </summary>
    /// <remarks>
    /// Self-enumerable — use directly in foreach:
    /// <code>foreach (var entity in queryBuilder.Entities()) { ... }</code>
    /// </remarks>
    public ref struct EntitiesQueryIterator
    {
        QueryIterator _inner;
        readonly WorldAccessor _world;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal EntitiesQueryIterator(QueryIterator inner, WorldAccessor world)
        {
            _inner = inner;
            _world = world;
        }

        public readonly EntityAccessor Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new EntityAccessor(_world, _inner.Current);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntitiesQueryIterator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _inner.MoveNext();
    }
}
