using System;
using System.ComponentModel;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class NativeIterableDictionarySerializer<TKey, TValue>
        : ISerializer<NativeIterableDictionary<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        public NativeIterableDictionarySerializer() { }

        public void Serialize(
            in NativeIterableDictionary<TKey, TValue> value,
            ISerializationWriter writer
        )
        {
            value.SerializeValues(writer);
        }

        public void Deserialize(
            ref NativeIterableDictionary<TKey, TValue> value,
            ISerializationReader reader
        )
        {
            value.DeserializeValues(reader);
        }
    }
}
