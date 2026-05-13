using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only safety-checked view over a single <see cref="NativeUniquePtr{T}"/> allocation.
    /// Obtain via <see cref="HeapAccessor.Read{T}"/> on the main thread, or
    /// <see cref="NativeUniquePtrResolver.Read{T}"/> in Burst jobs. Both paths fetch the
    /// underlying <c>AtomicSafetyHandle</c> from the heap's per-allocation pool so that
    /// cross-job conflicts (concurrent read+write or write+write on the same blob) are
    /// detected at schedule time without the walker needing to traverse anything else.
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeUniqueRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly void* _ptr;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeUniqueRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueRead(void* ptr, AtomicSafetyHandle safety)
        {
            _ptr = ptr;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeUniqueRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueRead(void* ptr)
        {
            _ptr = ptr;
        }
#endif

        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return ref UnsafeUtility.AsRef<T>(_ptr);
            }
        }
    }
}
