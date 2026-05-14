using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Writable safety-checked view over a <see cref="TrecsList{T}"/>. Supports indexed
    /// writes, <c>Add</c> up to capacity, <c>RemoveAt</c>, and <c>Clear</c>.
    ///
    /// <para><b>No auto-grow.</b> <see cref="Add"/> throws if <c>Count == Capacity</c>;
    /// callers must pre-size with
    /// <see cref="TrecsList{T}.EnsureCapacity(HeapAccessor, int)"/> on the main thread
    /// before scheduling a job that appends. The chunk-store-backed data buffer can only
    /// be reallocated from the main thread.</para>
    ///
    /// <para>The wrapper is exclusive: scheduling a second <see cref="TrecsListWrite{T}"/>
    /// (or a <see cref="TrecsListRead{T}"/>) over the same list while one is in flight is
    /// rejected by Unity's job-safety walker at <c>Schedule</c> time.</para>
    /// </summary>
    [NativeContainer]
    public readonly unsafe struct TrecsListWrite<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly TrecsListHeader* _header;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            TrecsListWrite<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsListWrite(TrecsListHeader* header, AtomicSafetyHandle safety)
        {
            _header = header;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<TrecsListWrite<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsListWrite(TrecsListHeader* header)
        {
            _header = header;
        }
#endif

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return _header->Count;
            }
        }

        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return _header->Capacity;
            }
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                TrecsAssert.That(
                    (uint)index < (uint)_header->Count,
                    "TrecsListWrite index {0} out of range (Count={1})",
                    index,
                    _header->Count
                );
                return ref ((T*)_header->Data)[index];
            }
        }

        /// <summary>
        /// Appends <paramref name="value"/>. Throws if <c>Count == Capacity</c> — grow the
        /// list with <see cref="TrecsList{T}.EnsureCapacity(HeapAccessor, int)"/> on the
        /// main thread before calling from a job.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsAssert.That(
                _header->Count < _header->Capacity,
                "TrecsListWrite.Add: capacity exceeded (count={0}, capacity={1}). "
                    + "Call TrecsList.EnsureCapacity on the main thread before scheduling.",
                _header->Count,
                _header->Capacity
            );
            ((T*)_header->Data)[_header->Count] = value;
            _header->Count += 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            _header->Count = 0;
        }

        /// <summary>Removes element at <paramref name="index"/>, shifting later elements down (O(N)).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsListWrite.RemoveAt index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            var data = (T*)_header->Data;
            var tail = _header->Count - index - 1;
            if (tail > 0)
            {
                UnsafeUtility.MemMove(data + index, data + index + 1, (long)tail * sizeof(T));
            }
            _header->Count -= 1;
        }

        /// <summary>
        /// Removes the element at <paramref name="index"/> by swapping the last element into
        /// its slot (O(1), does not preserve order).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAtSwapBack(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsListWrite.RemoveAtSwapBack index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            var data = (T*)_header->Data;
            var last = _header->Count - 1;
            if (index != last)
            {
                data[index] = data[last];
            }
            _header->Count -= 1;
        }
    }
}
