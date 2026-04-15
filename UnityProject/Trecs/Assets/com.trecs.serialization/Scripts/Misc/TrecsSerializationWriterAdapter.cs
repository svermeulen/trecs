using Trecs.Internal;

namespace Trecs.Serialization
{
    public struct TrecsSerializationWriterAdapter : ITrecsSerializationWriter
    {
        readonly ISerializationWriter _inner;

        public TrecsSerializationWriterAdapter(ISerializationWriter inner)
        {
            _inner = inner;
        }

        public ISerializationWriter Inner => _inner;

        public void Write<T>(string name, in T value) => _inner.Write(name, value);

        public void WriteObject(string name, object value) => _inner.WriteObject(name, value);

        public long NumBytesWritten => _inner.NumBytesWritten;

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes) =>
            _inner.BlitWriteRawBytes(name, ptr, numBytes);
    }
}
