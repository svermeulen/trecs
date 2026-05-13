using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Writable safety-checked view over a <see cref="TrecsList{T}"/>. Add / Remove
    /// operations may reallocate the backing data buffer when <c>Count == Capacity</c>;
    /// the header pointer is stable across grows, so the wrapper itself never goes stale.
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
                Assert.That(
                    (uint)index < (uint)_header->Count,
                    "TrecsListWrite index {} out of range (Count={})",
                    index,
                    _header->Count
                );
                return ref ((T*)_header->Data)[index];
            }
        }

        /// <summary>
        /// Appends <paramref name="value"/>, growing the backing buffer if needed.
        /// The first byte of the appended slot becomes <c>Count - 1</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            if (_header->Count == _header->Capacity)
            {
                Grow(_header->Capacity == 0 ? 4 : _header->Capacity * 2);
            }
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
            Assert.That(
                (uint)index < (uint)_header->Count,
                "TrecsListWrite.RemoveAt index {} out of range (Count={})",
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
            Assert.That(
                (uint)index < (uint)_header->Count,
                "TrecsListWrite.RemoveAtSwapBack index {} out of range (Count={})",
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

        /// <summary>
        /// Grows the backing buffer so that it can hold at least <paramref name="minCapacity"/>
        /// elements. No-op if already at or above. Doubles geometrically beyond <paramref name="minCapacity"/>
        /// to amortize.
        /// </summary>
        public void EnsureCapacity(int minCapacity)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            if (minCapacity > _header->Capacity)
            {
                var newCap = _header->Capacity == 0 ? 4 : _header->Capacity;
                while (newCap < minCapacity)
                {
                    newCap *= 2;
                }
                Grow(newCap);
            }
        }

        void Grow(int newCapacity)
        {
            Assert.That(newCapacity > _header->Capacity);
            var newData = AllocatorManager.Allocate(
                Allocator.Persistent,
                _header->ElementSize,
                _header->ElementAlign,
                newCapacity
            );
            if (_header->Count > 0)
            {
                UnsafeUtility.MemCpy(
                    newData,
                    _header->Data.ToPointer(),
                    (long)_header->Count * _header->ElementSize
                );
            }
            if (_header->Data != IntPtr.Zero)
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    _header->Data.ToPointer(),
                    _header->ElementSize,
                    _header->ElementAlign,
                    _header->Capacity
                );
            }
            _header->Data = new IntPtr(newData);
            _header->Capacity = newCapacity;
        }
    }
}
