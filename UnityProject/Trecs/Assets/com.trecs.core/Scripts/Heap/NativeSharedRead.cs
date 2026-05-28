using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only safety-checked view over a <see cref="NativeSharedPtr{T}"/> allocation.
    /// Obtain via <see cref="NativeSharedPtr{T}.Read(WorldAccessor)"/> on the main thread,
    /// or <see cref="NativeSharedPtr{T}.Read(in NativeWorldAccessor)"/> in Burst jobs.
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> The wrapper captures the
    /// slot's <c>NativeSharedHeapSideTableEntry.Generation</c> at Open and re-checks on
    /// every access. If the pointer was disposed and its side-table slot recycled,
    /// the check fires (~1/256 chance of coincidental match after slot reuse).
    /// This check runs unconditionally — not gated on ENABLE_UNITY_COLLECTIONS_CHECKS.</para>
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeSharedRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly void* _ptr;

        [NativeDisableUnsafePtrRestriction]
        readonly NativeSharedHeapSideTableEntry* _slot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeSharedRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeSharedRead(
            void* ptr,
            NativeSharedHeapSideTableEntry* slot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _ptr = ptr;
            _slot = slot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeSharedRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeSharedRead(
            void* ptr,
            NativeSharedHeapSideTableEntry* slot,
            byte capturedGeneration
        )
        {
            _ptr = ptr;
            _slot = slot;
            _capturedGeneration = capturedGeneration;
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            if (_slot == null)
                return;
            TrecsAssert.That(
                _slot->Generation == _capturedGeneration && _slot->InUse == 1,
                "NativeSharedRead is stale: the underlying NativeSharedPtr allocation has "
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
