using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only safety-checked view over a single <see cref="NativeUniquePtr{T}"/> allocation.
    /// Obtain via <see cref="WorldAccessor.Read{T}"/> on the main thread, or
    /// <see cref="NativeUniquePtr{T}.Read(in NativeHeapResolver)"/> in Burst jobs. Both paths fetch the
    /// underlying <c>AtomicSafetyHandle</c> from the heap's per-allocation pool so that
    /// cross-job conflicts (concurrent read+write or write+write on the same blob) are
    /// detected at schedule time without the walker needing to traverse anything else.
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> The wrapper captures the
    /// slot's <c>NativeHeapEntry.Generation</c> at Open and re-checks on
    /// every access. If the pointer was disposed and its side-table slot recycled,
    /// the check fires (1-byte generation, so ~1/256 chance of coincidental match
    /// after the slot is reused). This check runs unconditionally — closes the
    /// shipping-build hole where <c>AtomicSafetyHandle</c> is compiled out.</para>
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeUniqueRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly void* _ptr;

        // See class XML doc for the shipping-build use-after-dispose guard.
        [NativeDisableUnsafePtrRestriction]
        readonly NativeHeapEntry* _slot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeUniqueRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueRead(
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
            CollectionHelper.SetStaticSafetyId<NativeUniqueRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeUniqueRead(void* ptr, NativeHeapEntry* slot, byte capturedGeneration)
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
                "NativeUniqueRead is stale: the underlying NativeUniquePtr allocation has "
                    + "been freed since this wrapper was opened (captured slot gen {0}, "
                    + "current {1}, InUse {2}).",
                _capturedGeneration,
                _slot->Generation,
                _slot->InUse
            );
        }

        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                CheckSlotAlive();
                return ref UnsafeUtility.AsRef<T>(_ptr);
            }
        }
    }
}
