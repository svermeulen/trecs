using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="DenseDictionary{TKey,TValue}"/> — the
    /// deterministic, dense-indexed dictionary used by Trecs. Writes entries
    /// in their internal dense order so the wire format is stable across runs.
    /// </summary>
    public sealed class DenseDictionarySerializer<TKey, TValue>
        : ISerializer<DenseDictionary<TKey, TValue>>
        where TKey : struct, IEquatable<TKey>
    {
        public DenseDictionarySerializer() { }

        public void Deserialize(ref DenseDictionary<TKey, TValue> dict, ISerializationReader reader)
        {
            // Use blit path only for binary serialization with unmanaged types
            // JSON serialization doesn't support BlitReadArrayPtr
            if (
                DenseDictionaryBlitHelperCache<TKey, TValue>.CanUseBlit
                && reader is BinarySerializationReader
            )
            {
                if (dict == null)
                {
                    dict = new DenseDictionary<TKey, TValue>();
                }
                else
                {
                    TrecsAssert.That(dict.IsEmpty);
                }

                DenseDictionaryBlitHelperCache<TKey, TValue>.Helper.DeserializeBlit(
                    ref dict,
                    reader
                );
            }
            else
            {
                // Fallback to element-by-element serialization
                var numItems = reader.Read<int>("Count");

                if (dict == null)
                {
                    dict = new DenseDictionary<TKey, TValue>(numItems);
                }
                else
                {
                    TrecsAssert.That(dict.IsEmpty);
                    dict.EnsureCapacity(numItems);
                }

                for (int i = 0; i < numItems; i++)
                {
                    var key = reader.Read<TKey>("Key");
                    var value = reader.Read<TValue>("Value");

                    dict.Add(key, value);
                }
            }
        }

        public void Serialize(in DenseDictionary<TKey, TValue> value, ISerializationWriter writer)
        {
            // Use blit path only for binary serialization with unmanaged types
            // JSON serialization doesn't support BlitWriteArrayPtr
            if (
                DenseDictionaryBlitHelperCache<TKey, TValue>.CanUseBlit
                && writer is BinarySerializationWriter
            )
            {
                DenseDictionaryBlitHelperCache<TKey, TValue>.Helper.SerializeBlit(value, writer);
            }
            else
            {
                // Fallback to element-by-element serialization
                writer.Write<int>("Count", value.Count);

                foreach (var item in value)
                {
                    writer.Write("Key", item.Key);
                    writer.Write("Value", item.Value);
                }
            }
        }
    }
}
