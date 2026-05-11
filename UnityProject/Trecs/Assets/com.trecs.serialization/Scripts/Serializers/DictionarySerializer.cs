using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="Dictionary{TKey,TValue}"/>. Writes entry count
    /// followed by each (key, value) pair. Managed Dictionary does not
    /// guarantee ordering across runs, so this serializer is unsuitable for
    /// deterministic snapshots — prefer <see cref="DenseDictionarySerializer{TKey,TValue}"/>.
    /// </summary>
    public sealed class DictionarySerializer<TKey, TValue> : ISerializer<Dictionary<TKey, TValue>>
    {
        public DictionarySerializer() { }

        public void Deserialize(ref Dictionary<TKey, TValue> dict, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("count");
            Assert.That(numItems >= 0);

            if (dict == null)
            {
                dict = new Dictionary<TKey, TValue>(numItems);
            }
            else
            {
                Assert.That(dict.Count == 0);

                dict.Clear();
                dict.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                var key = reader.Read<TKey>("key");
                var value = reader.Read<TValue>("value");

                dict.Add(key, value);
            }
        }

        public void Serialize(in Dictionary<TKey, TValue> value, ISerializationWriter writer)
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
