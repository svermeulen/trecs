using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public sealed class IterableDictionarySerializer<TKey, TValue>
        : ISerializer<IterableDictionary<TKey, TValue>>
        where TKey : struct, IEquatable<TKey>
        where TValue : class
    {
        public IterableDictionarySerializer() { }

        public void Deserialize(
            ref IterableDictionary<TKey, TValue> dict,
            ISerializationReader reader
        )
        {
            var numItems = reader.Read<int>("Count");

            if (dict == null)
            {
                dict = new IterableDictionary<TKey, TValue>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(dict.IsEmpty);
                dict.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                var key = reader.Read<TKey>("Key");
                var value = reader.Read<TValue>("Value");

                dict.Add(key, value);
            }
        }

        public void Serialize(
            in IterableDictionary<TKey, TValue> value,
            ISerializationWriter writer
        )
        {
            writer.Write<int>("Count", value.Count);

            foreach (var item in value)
            {
                writer.Write("Key", item.Key);
                writer.Write("Value", item.Value);
            }
        }
    }
}
