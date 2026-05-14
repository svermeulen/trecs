using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only safety-checked view over a <see cref="TrecsList{T}"/>. Obtain via
    /// <see cref="HeapAccessor.Read{T}(in TrecsList{T})"/> on the main thread, or
    /// <see cref="NativeTrecsListResolver.Read{T}"/> in Burst jobs. Many readers may
    /// hold this concurrently without conflict because the wrapper is
    /// <c>[NativeContainerIsReadOnly]</c>; a concurrent <see cref="TrecsListWrite{T}"/>
    /// over the same list is rejected at <c>Schedule</c> time.
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct TrecsListRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly TrecsListHeader* _header;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            TrecsListRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsListRead(TrecsListHeader* header, AtomicSafetyHandle safety)
        {
            _header = header;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<TrecsListRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsListRead(TrecsListHeader* header)
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

        public ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                TrecsAssert.That(
                    (uint)index < (uint)_header->Count,
                    "TrecsListRead index {0} out of range (Count={1})",
                    index,
                    _header->Count
                );
                return ref ((T*)_header->Data)[index];
            }
        }
    }
}
