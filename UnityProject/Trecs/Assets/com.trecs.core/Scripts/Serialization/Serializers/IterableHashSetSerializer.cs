using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="IterableHashSet{T}"/> — the deterministic,
    /// dense-indexed hash-set used by Trecs. Writes the contents in their
    /// internal dense order so the wire format is stable across runs.
    /// </summary>
    public sealed class IterableHashSetSerializer<T> : ISerializer<IterableHashSet<T>>
        where T : struct, IEquatable<T>
    {
        public IterableHashSetSerializer() { }

        public void Deserialize(ref IterableHashSet<T> dict, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");

            if (dict == null)
            {
                dict = new IterableHashSet<T>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(dict.IsEmpty);

                dict.Clear();
                dict.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                var value = reader.Read<T>("Value");

                dict.Add(value);
            }
        }

        public void Serialize(in IterableHashSet<T> value, ISerializationWriter writer)
        {
            writer.Write<int>("Count", value.Count);

            foreach (var item in value)
            {
                writer.Write("Value", item);
            }
        }
    }
}
