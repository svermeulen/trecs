using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Blit serializer for <see cref="IterableHashSet{T}"/>. Elements are always unmanaged,
    /// so unlike the other collection serializers there is no Managed/Unmanaged split.
    /// Writes the backing dictionary's internal structure (nodes + buckets) as raw memory —
    /// the same shape as <see cref="IterableDictionarySerializerUnmanaged{TKey,TValue}"/>,
    /// minus the dummy values array a hash set carries. This avoids the per-element
    /// <c>ISerializationWriter.Write</c> path, which costs a serializer lookup plus generic
    /// interface dispatches per item (IL2CPP-expensive — ~300ns/element measured on large
    /// BlobId sets); the blit form is a handful of memcpys regardless of count.
    /// </summary>
    public sealed class IterableHashSetSerializer<T> : ISerializer<IterableHashSet<T>>
        where T : unmanaged, IEquatable<T>
    {
        public IterableHashSetSerializer() { }

        public void Deserialize(ref IterableHashSet<T> set, ISerializationReader reader)
        {
            if (set == null)
            {
                set = new IterableHashSet<T>();
            }
            else
            {
                TrecsDebugAssert.That(set.IsEmpty);
            }

            var dict = set.UnsafeInnerDictionary;

            reader.BlitRead("FreeValueCellIndex", ref dict.UnsafeFreeValueCellIndex);

            int bucketsCapacity = 0;
            reader.BlitRead("BucketsCapacity", ref bucketsCapacity);
            TrecsDebugAssert.That(bucketsCapacity >= 0);

            var count = dict.UnsafeFreeValueCellIndex;
            TrecsDebugAssert.That(count >= 0);

            dict.UnsafeEnsureCapacityForDeserialization(count, bucketsCapacity);

            unsafe
            {
                fixed (IterableDictionaryNode<T>* ptr = dict.UnsafeKeys)
                {
                    reader.BlitReadArrayPtr("ValuesInfo", ptr, count);
                }
            }

            // No "Values" section: the backing dictionary's values are empty placeholder
            // structs, and their array contents are never read — only the nodes matter.

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

        public void Serialize(in IterableHashSet<T> value, ISerializationWriter writer)
        {
            var dict = value.UnsafeInnerDictionary;

            var count = dict.UnsafeFreeValueCellIndex;
            var bucketsCapacity = dict.UnsafeBucketsCapacity;

            writer.BlitWrite("FreeValueCellIndex", count);
            writer.BlitWrite("BucketsCapacity", bucketsCapacity);

            var nodesBuffer = dict.UnsafeKeys;
            unsafe
            {
                fixed (IterableDictionaryNode<T>* ptr = nodesBuffer)
                {
                    writer.BlitWriteArrayPtr("ValuesInfo", ptr, count);
                }
            }

            var bucketsBuffer = dict.UnsafeBuckets;
            unsafe
            {
                fixed (int* ptr = bucketsBuffer)
                {
                    writer.BlitWriteArrayPtr("Buckets", ptr, bucketsCapacity);
                }
            }

            writer.BlitWrite("Collisions", dict.UnsafeCollisions);
            writer.BlitWrite("FastModBucketsMultiplier", dict.UnsafeFastModBucketsMultiplier);
        }
    }
}
