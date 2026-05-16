using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Flat entity iterator that yields <see cref="EntityHandle"/> values for all entities
    /// matching a query. Wraps the underlying index iterator and resolves each index to
    /// its stable handle on access.
    /// </summary>
    /// <remarks>
    /// Self-enumerable — use directly in foreach:
    /// <code>foreach (var handle in queryBuilder.Handles()) { ... }</code>
    /// </remarks>
    public ref struct HandleQueryIterator
    {
        IndexQueryIterator _inner;
        readonly WorldAccessor _world;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal HandleQueryIterator(IndexQueryIterator inner, WorldAccessor world)
        {
            _inner = inner;
            _world = world;
        }

        public readonly EntityHandle Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _inner.Current.ToHandle(_world);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HandleQueryIterator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _inner.MoveNext();
    }
}
