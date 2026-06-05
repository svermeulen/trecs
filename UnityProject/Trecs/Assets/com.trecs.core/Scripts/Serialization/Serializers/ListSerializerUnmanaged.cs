using System;
using System.Collections;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="List{T}"/> with unmanaged elements. Writes the
    /// item count followed by the elements as a single blit, avoiding the
    /// per-element name/type framing that <see cref="ListSerializerManaged{T}"/>
    /// incurs.
    /// </summary>
    public sealed class ListSerializerUnmanaged<T> : ISerializer<List<T>>
        where T : unmanaged
    {
        // Staging buffer for moving elements between the blit stream and the
        // list — List<T> exposes no view of its backing array on Unity's
        // .NET Standard 2.1 surface (CollectionsMarshal.AsSpan is .NET 5+),
        // so the contiguous copy is unavoidable, but the buffer itself is
        // grow-only and reused across calls. The registry caches one
        // serializer instance per closed type and serialization runs on the
        // main thread, so no pooling or locking is needed.
        T[] _scratch = Array.Empty<T>();

        readonly ScratchPrefix _scratchPrefix;

        public ListSerializerUnmanaged()
        {
            _scratchPrefix = new ScratchPrefix(this);
        }

        T[] GetScratch(int count)
        {
            if (_scratch.Length < count)
            {
                _scratch = new T[Math.Max(count, _scratch.Length * 2)];
            }

            return _scratch;
        }

        public void Deserialize(ref List<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            if (value == null)
            {
                value = new List<T>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(value.Count == 0);
                value.Clear();

                if (value.Capacity < numItems)
                {
                    value.Capacity = numItems;
                }
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

            // The scratch is usually larger than numItems and .NET Standard
            // 2.1 has no AddRange(T[], count) overload, so hand AddRange a
            // reusable ICollection view of the prefix — AddRange's
            // ICollection<T> fast path then does the append as a single
            // Array.Copy instead of numItems individual Adds.
            _scratchPrefix.Count = numItems;
            value.AddRange(_scratchPrefix);
            _scratchPrefix.Count = 0;
        }

        public void Serialize(in List<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Count);

            if (value.Count == 0)
            {
                return;
            }

            var buffer = GetScratch(value.Count);
            value.CopyTo(buffer);

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    writer.BlitWriteArrayPtr("Items", ptr, value.Count);
                }
            }
        }

        // List<T>.AddRange/InsertRange type-check their argument for
        // ICollection<T> and then use only Count and CopyTo, turning the
        // append into a single Array.Copy. This adapter exposes the first
        // Count elements of the owner's scratch through that interface so
        // the fast path applies without boxing an ArraySegment per call.
        sealed class ScratchPrefix : ICollection<T>
        {
            readonly ListSerializerUnmanaged<T> _owner;

            public ScratchPrefix(ListSerializerUnmanaged<T> owner)
            {
                _owner = owner;
            }

            public int Count { get; set; }

            public bool IsReadOnly => true;

            public void CopyTo(T[] array, int arrayIndex)
            {
                Array.Copy(_owner._scratch, 0, array, arrayIndex, Count);
            }

            // Defensive completeness — AddRange's ICollection<T> fast path
            // never enumerates.
            public IEnumerator<T> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return _owner._scratch[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public void Add(T item) => throw new NotSupportedException();

            public void Clear() => throw new NotSupportedException();

            public bool Contains(T item) => throw new NotSupportedException();

            public bool Remove(T item) => throw new NotSupportedException();
        }
    }
}
