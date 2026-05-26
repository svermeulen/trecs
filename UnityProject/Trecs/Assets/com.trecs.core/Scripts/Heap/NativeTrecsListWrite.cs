using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Burst-safe writable view over a <see cref="TrecsList{T}"/>. Supports indexed
    /// writes, <c>Add</c> up to capacity, <c>RemoveAt</c>, and <c>Clear</c>.
    ///
    /// <para><b>No auto-grow.</b> <see cref="Add"/> throws if <c>Count == Capacity</c>;
    /// pre-size with
    /// <see cref="TrecsListExtensions.EnsureCapacity{T}(ref TrecsList{T}, WorldAccessor, int, string, int)"/>
    /// on the main thread before scheduling the job, or use the managed
    /// <see cref="TrecsListWrite{T}"/> wrapper which auto-grows.</para>
    ///
    /// <para>The wrapper is exclusive: scheduling a second <see cref="NativeTrecsListWrite{T}"/>
    /// (or a <see cref="NativeTrecsListRead{T}"/>) over the same list while one is in
    /// flight is rejected by Unity's job-safety walker at <c>Schedule</c> time.</para>
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> See
    /// <see cref="TrecsListRead{T}"/> for the rationale.</para>
    /// </summary>
    [NativeContainer]
    public unsafe struct NativeTrecsListWrite<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly TrecsListHeader* _header;

        // Resolved from header->DataHandle at Open time. Null only if the list has no
        // backing buffer; Count is necessarily 0 in that case so the indexer's bounds
        // check (or Add's capacity check) prevents access.
        [NativeDisableUnsafePtrRestriction]
        readonly T* _data;

        [NativeDisableUnsafePtrRestriction]
        readonly NativeHeapEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeTrecsListWrite<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsListWrite(
            TrecsListHeader* header,
            T* data,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _header = header;
            _data = data;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeTrecsListWrite<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
            CheckSlotAlive();
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsListWrite(
            TrecsListHeader* header,
            T* data,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration
        )
        {
            _header = header;
            _data = data;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            CheckSlotAlive();
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            TrecsAssert.That(
                _headerSlot->Generation == _capturedGeneration && _headerSlot->InUse == 1,
                "NativeTrecsListWrite is stale: the underlying TrecsList allocation has been "
                    + "freed since this wrapper was opened (captured slot gen {0}, current {1}, "
                    + "InUse {2}).",
                _capturedGeneration,
                _headerSlot->Generation,
                _headerSlot->InUse
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void BumpVersion()
        {
            unchecked
            {
                ++_header->Version;
            }
        }

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
                TrecsDebugAssert.That(
                    (uint)index < (uint)_header->Count,
                    "NativeTrecsListWrite index {0} out of range (Count={1})",
                    index,
                    _header->Count
                );
                return ref _data[index];
            }
        }

        /// <summary>
        /// Appends <paramref name="value"/>. Throws if <c>Count == Capacity</c> — pre-size
        /// the list with
        /// <see cref="TrecsListExtensions.EnsureCapacity{T}(ref TrecsList{T}, WorldAccessor, int, string, int)"/>
        /// on the main thread before scheduling, or use the managed
        /// <see cref="TrecsListWrite{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsDebugAssert.That(
                _header->Count < _header->Capacity,
                "NativeTrecsListWrite.Add: capacity exceeded (count={0}, capacity={1}). "
                    + "Pre-size with TrecsList.EnsureCapacity on the main thread before "
                    + "scheduling, or use the managed TrecsListWrite which auto-grows.",
                _header->Count,
                _header->Capacity
            );
            _data[_header->Count] = value;
            _header->Count += 1;
            BumpVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            _header->Count = 0;
            BumpVersion();
        }

        /// <summary>Removes element at <paramref name="index"/>, shifting later elements down (O(N)).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "NativeTrecsListWrite.RemoveAt index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            var data = _data;
            var tail = _header->Count - index - 1;
            if (tail > 0)
            {
                UnsafeUtility.MemMove(data + index, data + index + 1, (long)tail * sizeof(T));
            }
            _header->Count -= 1;
            BumpVersion();
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
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "NativeTrecsListWrite.RemoveAtSwapBack index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            var data = _data;
            var last = _header->Count - 1;
            if (index != last)
            {
                data[index] = data[last];
            }
            _header->Count -= 1;
            BumpVersion();
        }
    }
}
