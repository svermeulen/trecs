using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Writable safety-checked view over a single <see cref="NativeUniquePtr{T}"/> allocation.
    /// See <see cref="NativeUniqueRead{T}"/> for safety details.
    /// </summary>
    [NativeContainer]
    public readonly unsafe struct NativeUniqueWrite<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly void* _ptr;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeUniqueWrite<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueWrite(void* ptr, AtomicSafetyHandle safety)
        {
            _ptr = ptr;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeUniqueWrite<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueWrite(void* ptr)
        {
            _ptr = ptr;
        }
#endif

        public ref T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                return ref UnsafeUtility.AsRef<T>(_ptr);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(in T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            UnsafeUtility.AsRef<T>(_ptr) = value;
        }
    }
}
