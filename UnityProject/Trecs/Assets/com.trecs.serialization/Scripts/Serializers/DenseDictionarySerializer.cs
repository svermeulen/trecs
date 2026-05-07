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
            if (DenseDictionaryBlitHelperCache<TKey, TValue>.CanUseBlit)
            {
                if (dict == null)
                {
                    dict = new DenseDictionary<TKey, TValue>();
                }
                else
                {
                    Assert.That(dict.IsEmpty);
                }

                DenseDictionaryBlitHelperCache<TKey, TValue>.Helper.DeserializeBlit(
                    ref dict,
                    reader
                );
            }
            else
            {
                var numItems = reader.Read<int>("count");

                if (dict == null)
                {
                    dict = new DenseDictionary<TKey, TValue>(numItems);
                }
                else
                {
                    Assert.That(dict.IsEmpty);
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
            if (DenseDictionaryBlitHelperCache<TKey, TValue>.CanUseBlit)
            {
                DenseDictionaryBlitHelperCache<TKey, TValue>.Helper.SerializeBlit(value, writer);
            }
            else
            {
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
