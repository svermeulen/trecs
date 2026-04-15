namespace Trecs
{
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
