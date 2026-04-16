namespace Trecs
{
    /// <summary>
    /// Read-only single-entity component accessor for use in Burst jobs. Wraps a buffer
    /// and index pair, exposing the component via <see cref="Value"/>. Obtained from
    /// <c>[FromWorld]</c> fields on job structs.
    /// </summary>
    public readonly struct NativeComponentRead<T>
        where T : unmanaged, IEntityComponent
    {
        readonly NativeComponentBufferRead<T> _buffer;
        readonly int _index;

        public NativeComponentRead(NativeComponentBufferRead<T> buffer, int index)
        {
            _buffer = buffer;
            _index = index;
        }

        public ref readonly T Value
        {
            get => ref _buffer[_index];
        }
    }
}
