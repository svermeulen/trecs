namespace Trecs
{
    /// <summary>
    /// Read-write single-entity component accessor for use in Burst jobs. Wraps a buffer
    /// and index pair, exposing the component via a mutable <see cref="Value"/> ref.
    /// Obtained from <c>[FromWorld]</c> fields on job structs.
    /// </summary>
    public readonly struct NativeComponentWrite<T>
        where T : unmanaged, IEntityComponent
    {
        readonly NativeComponentBufferWrite<T> _buffer;
        readonly int _index;

        public NativeComponentWrite(NativeComponentBufferWrite<T> buffer, int index)
        {
            _buffer = buffer;
            _index = index;
        }

        public ref T Value
        {
            get => ref _buffer[_index];
        }
    }
}
