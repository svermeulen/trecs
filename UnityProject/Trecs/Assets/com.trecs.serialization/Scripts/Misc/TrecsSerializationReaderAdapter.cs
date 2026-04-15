using Trecs.Internal;

namespace Trecs.Serialization
{
    public struct TrecsSerializationReaderAdapter : ITrecsSerializationReader
    {
        readonly ISerializationReader _inner;

        public TrecsSerializationReaderAdapter(ISerializationReader inner)
        {
            _inner = inner;
        }

        public ISerializationReader Inner => _inner;

        public void Read<T>(string name, ref T value) => _inner.Read(name, ref value);

        public void ReadObject(string name, ref object value) => _inner.ReadObject(name, ref value);

        public unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes) =>
            _inner.BlitReadRawBytes(name, ptr, numBytes);
    }
}
