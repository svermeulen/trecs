using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="IterableDictionary{TKey,TValue}"/> with unmanaged keys
    /// and managed/custom values. Writes the item count, then all keys as a single blit
    /// (in iteration order), then each value via its registered serializer — so only the
    /// values pay per-element framing. For unmanaged values use
    /// <see cref="IterableDictionarySerializerUnmanaged{TKey,TValue}"/> instead, which
    /// blits the dictionary's internal structure wholesale.
    /// </summary>
    public sealed class IterableDictionarySerializerManaged<TKey, TValue>
        : ISerializer<IterableDictionary<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : class
    {
        // Staging buffer for the key blit — grow-only and reused across
        // calls. The registry caches one serializer instance per closed type
        // and serialization runs on the main thread, so no pooling or
        // locking is needed.
        TKey[] _keyScratch = Array.Empty<TKey>();

        public IterableDictionarySerializerManaged() { }

        TKey[] GetKeyScratch(int count)
        {
            if (_keyScratch.Length < count)
            {
                _keyScratch = new TKey[Math.Max(count, _keyScratch.Length * 2)];
            }

            return _keyScratch;
        }

        public void Deserialize(
            ref IterableDictionary<TKey, TValue> dict,
            ISerializationReader reader
        )
        {
            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            if (dict == null)
            {
                dict = new IterableDictionary<TKey, TValue>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(dict.IsEmpty);
                dict.EnsureCapacity(numItems);
            }

            if (numItems == 0)
            {
                return;
            }

            var keys = GetKeyScratch(numItems);

            unsafe
            {
                fixed (TKey* ptr = keys)
                {
                    reader.BlitReadArrayPtr("Keys", ptr, numItems);
                }
            }

            for (int i = 0; i < numItems; i++)
            {
                var value = reader.Read<TValue>("Value");
                dict.Add(keys[i], value);
            }
        }

        public void Serialize(
            in IterableDictionary<TKey, TValue> value,
            ISerializationWriter writer
        )
        {
            var count = value.Count;
            writer.Write<int>("Count", count);

            if (count == 0)
            {
                return;
            }

            var keys = GetKeyScratch(count);

            int numKeys = 0;
            foreach (var item in value)
            {
                keys[numKeys++] = item.Key;
            }

            TrecsDebugAssert.That(numKeys == count);

            unsafe
            {
                fixed (TKey* ptr = keys)
                {
                    writer.BlitWriteArrayPtr("Keys", ptr, count);
                }
            }

            foreach (var item in value)
            {
                writer.Write("Value", item.Value);
            }
        }
    }
}
