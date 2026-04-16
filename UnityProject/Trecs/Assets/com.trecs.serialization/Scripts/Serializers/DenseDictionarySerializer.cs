using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class DenseDictionarySerializer<TKey, TValue>
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
                    Assert.That(dict.IsEmpty());
                }

                DenseDictionaryBlitHelperCache<TKey, TValue>.Helper.DeserializeBlit(
                    ref dict,
                    reader
                );
            }
            else
            {
                // Fallback to element-by-element serialization
                var numItems = reader.Read<int>("count");
                Assert.That(numItems >= 0);

                if (dict == null)
                {
                    dict = new DenseDictionary<TKey, TValue>(numItems);
                }
                else
                {
                    Assert.That(dict.IsEmpty());
                    dict.EnsureCapacity(numItems);
                }

                for (int i = 0; i < numItems; i++)
                {
                    var key = reader.Read<TKey>("key");
                    var value = reader.Read<TValue>("value");

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
                writer.Write<int>("count", value.Count);

                foreach (var item in value)
                {
                    writer.Write("key", item.Key);
                    writer.Write("value", item.Value);
                }
            }
        }
    }
}
