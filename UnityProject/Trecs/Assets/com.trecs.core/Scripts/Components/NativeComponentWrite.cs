namespace Trecs
{
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
