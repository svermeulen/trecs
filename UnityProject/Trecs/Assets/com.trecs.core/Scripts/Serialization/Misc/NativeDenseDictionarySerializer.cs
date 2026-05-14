using System;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class NativeDenseDictionarySerializer<TKey, TValue>
        : ISerializer<NativeDenseDictionary<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        public NativeDenseDictionarySerializer() { }

        public void Serialize(
            in NativeDenseDictionary<TKey, TValue> value,
            ISerializationWriter writer
        )
        {
            value.SerializeValues(writer);
        }

        public void Deserialize(
            ref NativeDenseDictionary<TKey, TValue> value,
            ISerializationReader reader
        )
        {
            value.DeserializeValues(reader);
        }
    }
}
