using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Interface for blit serialization of DenseDictionary without unmanaged constraint.
    /// This allows the serializer to dispatch to a concrete implementation at runtime.
    /// </summary>
    public interface IDenseDictionaryBlitHelper<TKey, TValue>
        where TKey : struct, IEquatable<TKey>
    {
        void SerializeBlit(in DenseDictionary<TKey, TValue> dict, ISerializationWriter writer);
        void DeserializeBlit(ref DenseDictionary<TKey, TValue> dict, ISerializationReader reader);
    }

    /// <summary>
    /// Concrete implementation of blit serialization for DenseDictionary when both
    /// TKey and TValue are unmanaged types. Uses the Unsafe* accessors on DenseDictionary
    /// for zero-allocation full state serialization.
    /// </summary>
    internal class DenseDictionaryBlitHelperImpl<TKey, TValue>
        : IDenseDictionaryBlitHelper<TKey, TValue>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        public void SerializeBlit(
            in DenseDictionary<TKey, TValue> dict,
            ISerializationWriter writer
        )
        {
            var count = (int)dict.UnsafeFreeValueCellIndex;
            var bucketsCapacity = dict.UnsafeBucketsCapacity;

            // Write sizes first so deserialize can allocate before reading arrays
            writer.BlitWrite("_freeValueCellIndex", dict.UnsafeFreeValueCellIndex);
            writer.BlitWrite("_bucketsCapacity", bucketsCapacity);

            // Blit the Node array (contains hashcode, previous, key)
            var nodesBuffer = dict.UnsafeKeys;
            unsafe
            {
                fixed (DenseDictionary<TKey, TValue>.Node* ptr = nodesBuffer)
                {
                    writer.BlitWriteArrayPtr("_valuesInfo", ptr, count);
                }
            }

            // Blit the values array
            var valuesBuffer = dict.UnsafeValues;
            unsafe
            {
                fixed (TValue* ptr = valuesBuffer)
                {
                    writer.BlitWriteArrayPtr("_values", ptr, count);
                }
            }

            // Blit buckets array
            var bucketsBuffer = dict.UnsafeBuckets;
            unsafe
            {
                fixed (int* ptr = bucketsBuffer)
                {
                    writer.BlitWriteArrayPtr("_buckets", ptr, bucketsCapacity);
                }
            }

            writer.BlitWrite("_collisions", dict.UnsafeCollisions);
            writer.BlitWrite("_fastModBucketsMultiplier", dict.UnsafeFastModBucketsMultiplier);
        }

        public void DeserializeBlit(
            ref DenseDictionary<TKey, TValue> dict,
            ISerializationReader reader
        )
        {
            // Read sizes first
            reader.BlitRead("_freeValueCellIndex", ref dict.UnsafeFreeValueCellIndex);

            int bucketsCapacity = 0;
            reader.BlitRead("_bucketsCapacity", ref bucketsCapacity);
            Assert.That(bucketsCapacity >= 0);

            var count = (int)dict.UnsafeFreeValueCellIndex;
            Assert.That(count >= 0);

            // Ensure arrays are large enough before reading into them
            dict.UnsafeEnsureCapacityForDeserialization(count, bucketsCapacity);

            // Blit read directly into the internal arrays
            unsafe
            {
                fixed (DenseDictionary<TKey, TValue>.Node* ptr = dict.UnsafeKeys)
                {
                    reader.BlitReadArrayPtr("_valuesInfo", ptr, count);
                }
            }

            unsafe
            {
                fixed (TValue* ptr = dict.UnsafeValues)
                {
                    reader.BlitReadArrayPtr("_values", ptr, count);
                }
            }

            unsafe
            {
                fixed (int* ptr = dict.UnsafeBuckets)
                {
                    reader.BlitReadArrayPtr("_buckets", ptr, bucketsCapacity);
                }
            }

            reader.BlitRead("_collisions", ref dict.UnsafeCollisions);
            reader.BlitRead("_fastModBucketsMultiplier", ref dict.UnsafeFastModBucketsMultiplier);
        }
    }

    /// <summary>
    /// Static cache that determines at type load time whether blit serialization
    /// can be used for a given DenseDictionary type combination.
    /// </summary>
    public static class DenseDictionaryBlitHelperCache<TKey, TValue>
        where TKey : struct, IEquatable<TKey>
    {
        public static readonly bool CanUseBlit;
        public static readonly IDenseDictionaryBlitHelper<TKey, TValue> Helper;

        static DenseDictionaryBlitHelperCache()
        {
            // Check if both key and value types are unmanaged at runtime
            // Also verify that Node struct is unmanaged (it will be if TKey is)
            CanUseBlit =
                TypeMeta<TKey>.IsUnmanaged
                && TypeMeta<TValue>.IsUnmanaged
                && TypeMeta<DenseDictionary<TKey, TValue>.Node>.IsUnmanaged;

            if (CanUseBlit)
            {
                // Create the concrete helper via Activator
                // This is IL2CPP safe since it happens once at type load time
                var helperType = typeof(DenseDictionaryBlitHelperImpl<,>).MakeGenericType(
                    typeof(TKey),
                    typeof(TValue)
                );
                Helper =
                    (IDenseDictionaryBlitHelper<TKey, TValue>)Activator.CreateInstance(helperType);
            }
        }
    }
}
