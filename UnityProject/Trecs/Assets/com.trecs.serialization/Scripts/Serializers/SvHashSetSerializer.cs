using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class DenseHashSetSerializer<T> : ISerializer<DenseHashSet<T>>
        where T : struct, IEquatable<T>
    {
        public DenseHashSetSerializer() { }

        public void Deserialize(ref DenseHashSet<T> dict, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("count");

            if (dict == null)
            {
                dict = new DenseHashSet<T>(numItems);
            }
            else
            {
                Assert.That(dict.IsEmpty());

                dict.Clear();
                dict.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                var value = reader.Read<T>("value");

                dict.Add(value);
            }
        }

        public void Serialize(in DenseHashSet<T> value, ISerializationWriter writer)
        {
            writer.Write<int>("count", value.Count);

            foreach (var item in value)
            {
                writer.Write("value", item);
            }
        }
    }
}
