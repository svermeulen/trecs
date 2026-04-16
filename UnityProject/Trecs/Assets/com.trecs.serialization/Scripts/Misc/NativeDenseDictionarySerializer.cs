using System;
using System.ComponentModel;
using Trecs.Serialization;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class NativeDenseDictionarySerializer<TKey, TValue>
        : ISerializer<NativeDenseDictionary<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        static readonly TrecsLog _log = new("NativeDenseDictionarySerializer");

        public NativeDenseDictionarySerializer() { }

        public void Serialize(
            in NativeDenseDictionary<TKey, TValue> value,
            ISerializationWriter writer
        )
        {
            value.SerializeValues(new TrecsSerializationWriterAdapter(writer));
        }

        public void Deserialize(
            ref NativeDenseDictionary<TKey, TValue> value,
            ISerializationReader reader
        )
        {
            value.DeserializeValues(new TrecsSerializationReaderAdapter(reader));
        }
    }
}
