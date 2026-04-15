using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface ITrecsSerializationWriter
    {
        void Write<T>(string name, in T value);
        void WriteObject(string name, object value);
        long NumBytesWritten { get; }
        unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes);
    }
}
