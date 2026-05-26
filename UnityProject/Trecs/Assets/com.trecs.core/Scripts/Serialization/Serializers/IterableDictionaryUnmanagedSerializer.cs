using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public sealed class IterableDictionaryUnmanagedSerializer<TKey, TValue>
        : ISerializer<IterableDictionary<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        public IterableDictionaryUnmanagedSerializer() { }

        public void Deserialize(
            ref IterableDictionary<TKey, TValue> dict,
            ISerializationReader reader
        )
        {
            if (dict == null)
            {
                dict = new IterableDictionary<TKey, TValue>();
            }
            else
            {
                TrecsDebugAssert.That(dict.IsEmpty);
            }

            reader.BlitRead("FreeValueCellIndex", ref dict.UnsafeFreeValueCellIndex);

            int bucketsCapacity = 0;
            reader.BlitRead("BucketsCapacity", ref bucketsCapacity);
            TrecsDebugAssert.That(bucketsCapacity >= 0);

            var count = dict.UnsafeFreeValueCellIndex;
            TrecsDebugAssert.That(count >= 0);

            dict.UnsafeEnsureCapacityForDeserialization(count, bucketsCapacity);

            unsafe
            {
                fixed (IterableDictionaryNode<TKey>* ptr = dict.UnsafeKeys)
                {
                    reader.BlitReadArrayPtr("ValuesInfo", ptr, count);
                }
            }

            unsafe
            {
                fixed (TValue* ptr = dict.UnsafeValues)
                {
                    reader.BlitReadArrayPtr("Values", ptr, count);
                }
            }

            unsafe
            {
                fixed (int* ptr = dict.UnsafeBuckets)
                {
                    reader.BlitReadArrayPtr("Buckets", ptr, bucketsCapacity);
                }
            }

            reader.BlitRead("Collisions", ref dict.UnsafeCollisions);
            reader.BlitRead("FastModBucketsMultiplier", ref dict.UnsafeFastModBucketsMultiplier);
        }

        public void Serialize(
            in IterableDictionary<TKey, TValue> value,
            ISerializationWriter writer
        )
        {
            var count = value.UnsafeFreeValueCellIndex;
            var bucketsCapacity = value.UnsafeBucketsCapacity;

            writer.BlitWrite("FreeValueCellIndex", value.UnsafeFreeValueCellIndex);
            writer.BlitWrite("BucketsCapacity", bucketsCapacity);

            var nodesBuffer = value.UnsafeKeys;
            unsafe
            {
                fixed (IterableDictionaryNode<TKey>* ptr = nodesBuffer)
                {
                    writer.BlitWriteArrayPtr("ValuesInfo", ptr, count);
                }
            }

            var valuesBuffer = value.UnsafeValues;
            unsafe
            {
                fixed (TValue* ptr = valuesBuffer)
                {
                    writer.BlitWriteArrayPtr("Values", ptr, count);
                }
            }

            var bucketsBuffer = value.UnsafeBuckets;
            unsafe
            {
                fixed (int* ptr = bucketsBuffer)
                {
                    writer.BlitWriteArrayPtr("Buckets", ptr, bucketsCapacity);
                }
            }

            writer.BlitWrite("Collisions", value.UnsafeCollisions);
            writer.BlitWrite("FastModBucketsMultiplier", value.UnsafeFastModBucketsMultiplier);
        }
    }
}
