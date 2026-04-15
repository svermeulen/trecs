using System;
using System.ComponentModel;
using Trecs.Serialization;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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
            // Note that this method is very hot
            // Can be called ~1k times when serializing game state
            // This is why we directly use BlitWrite / BlitRead

            int count = value.Count;
            writer.BlitWrite("count", count);

            using (TrecsProfiling.Start("serializing _keys"))
            {
                unsafe
                {
                    writer.BlitWriteArrayPtr(
                        "keys",
                        NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(value._keys),
                        count
                    );
                }
            }

            using (TrecsProfiling.Start("serializing _values"))
            {
                unsafe
                {
                    writer.BlitWriteArrayPtr(
                        "values",
                        NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(value._values),
                        count
                    );
                }
            }
        }

        public void Deserialize(
            ref NativeDenseDictionary<TKey, TValue> value,
            ISerializationReader reader
        )
        {
            int count = default;
            reader.BlitRead("count", ref count);
            Assert.That(count >= 0);

            // Clear existing data
            value._keyToIndex.Clear();
            value._values.Clear();
            value._keys.Clear();

            // Ensure capacity
            if (value._values.Capacity < count)
            {
                value._values.Capacity = count;
                value._keys.Capacity = count;
            }

            if (value._keyToIndex.Capacity < count)
            {
                value._keyToIndex.Capacity = count;
            }

            // Read keys
            value._keys.Resize(count, NativeArrayOptions.UninitializedMemory);
            unsafe
            {
                reader.BlitReadArrayPtr(
                    "keys",
                    NativeListUnsafeUtility.GetUnsafePtr(value._keys),
                    count
                );
            }

            // Read values
            value._values.Resize(count, NativeArrayOptions.UninitializedMemory);
            unsafe
            {
                reader.BlitReadArrayPtr(
                    "values",
                    NativeListUnsafeUtility.GetUnsafePtr(value._values),
                    count
                );
            }

            // Rebuild hash map from keys
            for (int i = 0; i < count; i++)
            {
                value._keyToIndex.Add(value._keys[i], i);
            }
        }
    }
}
