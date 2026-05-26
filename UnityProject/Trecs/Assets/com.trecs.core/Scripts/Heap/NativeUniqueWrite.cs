using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Writable safety-checked view over a single <see cref="NativeUniquePtr{T}"/> allocation.
    /// See <see cref="NativeUniqueRead{T}"/> for safety details, including the
    /// shipping-build use-after-dispose guard.
    /// </summary>
    [NativeContainer]
    public readonly unsafe struct NativeUniqueWrite<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly void* _ptr;

        // See NativeUniqueRead<T> for the shipping-build use-after-dispose guard.
        [NativeDisableUnsafePtrRestriction]
        readonly NativeHeapEntry* _slot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeUniqueWrite<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueWrite(
            void* ptr,
            NativeHeapEntry* slot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _ptr = ptr;
            _slot = slot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeUniqueWrite<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueWrite(void* ptr, NativeHeapEntry* slot, byte capturedGeneration)
        {
            _ptr = ptr;
            _slot = slot;
            _capturedGeneration = capturedGeneration;
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            TrecsAssert.That(
                _slot->Generation == _capturedGeneration && _slot->InUse == 1,
                "NativeUniqueWrite is stale: the underlying NativeUniquePtr allocation has "
                    + "been freed since this wrapper was opened (captured slot gen {0}, "
                    + "current {1}, InUse {2}).",
                _capturedGeneration,
                _slot->Generation,
                _slot->InUse
            );
        }

        public ref T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                CheckSlotAlive();
                return ref UnsafeUtility.AsRef<T>(_ptr);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(in T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            UnsafeUtility.AsRef<T>(_ptr) = value;
        }
    }
}
