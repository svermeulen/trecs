using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="Queue{T}"/> with unmanaged elements. Writes
    /// the item count followed by the elements in FIFO order as a single
    /// blit, avoiding the per-element name/type framing that
    /// <see cref="QueueSerializerManaged{T}"/> incurs.
    /// </summary>
    public sealed class QueueSerializerUnmanaged<T> : ISerializer<Queue<T>>
        where T : unmanaged
    {
        // Staging buffer for moving elements between the blit stream and the
        // queue — Queue<T> exposes no view of its ring buffer, so the
        // contiguous copy is unavoidable, but the buffer itself is grow-only
        // and reused across calls. The registry caches one serializer
        // instance per closed type and serialization runs on the main
        // thread, so no pooling or locking is needed.
        T[] _scratch = Array.Empty<T>();

        public QueueSerializerUnmanaged() { }

        T[] GetScratch(int count)
        {
            if (_scratch.Length < count)
            {
                _scratch = new T[Math.Max(count, _scratch.Length * 2)];
            }

            return _scratch;
        }

        public void Deserialize(ref Queue<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            // Queue<T> in Unity's .NET Standard 2.1 surface has no
            // EnsureCapacity (added in .NET 5); pre-size via the constructor
            // when allocating fresh, otherwise reuse the existing backing
            // array.
            if (value == null)
            {
                value = new Queue<T>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(value.Count == 0);
                value.Clear();
            }

            if (numItems == 0)
            {
                return;
            }

            var buffer = GetScratch(numItems);

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    reader.BlitReadArrayPtr("Items", ptr, numItems);
                }
            }

            for (int i = 0; i < numItems; i++)
            {
                value.Enqueue(buffer[i]);
            }
        }

        public void Serialize(in Queue<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Count);

            if (value.Count == 0)
            {
                return;
            }

            var buffer = GetScratch(value.Count);
            value.CopyTo(buffer, 0);

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    writer.BlitWriteArrayPtr("Items", ptr, value.Count);
                }
            }
        }
    }
}
