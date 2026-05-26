using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Burst-safe writable view over a <see cref="TrecsArray{T}"/>. Obtain via the
    /// <c>Write</c> extension methods on <see cref="TrecsArrayExtensions"/>. Works
    /// both on the main thread and inside <c>[BurstCompile]</c> jobs.
    ///
    /// <para>The only mutation operation is indexed assignment (<c>arr[i] = v</c>).
    /// Arrays don't grow — to change the size, dispose and re-allocate. The
    /// wrapper is exclusive: scheduling a second <see cref="TrecsArrayWrite{T}"/>
    /// (or a <see cref="TrecsArrayRead{T}"/>) over the same array while one is in
    /// flight is rejected by Unity's job-safety walker at <c>Schedule</c> time.</para>
    ///
    /// <para>The data buffer pointer and length are cached at Open time, so element
    /// access is a direct <c>_data[index]</c> with no per-access indirection. Since
    /// the array can't be resized, the cached pointer is stable for the wrapper's
    /// lifetime — no version stamp is needed.</para>
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> See
    /// <see cref="TrecsArrayRead{T}"/> for the rationale.</para>
    /// </summary>
    [NativeContainer]
    public readonly unsafe struct TrecsArrayWrite<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly T* _data;

        readonly int _length;

        // See TrecsArrayRead<T> for the shipping-build use-after-dispose guard. For
        // the Length=0 / null-handle path, _slot is null and CheckSlotAlive is
        // skipped — see TrecsArrayRead<T>.CheckSlotAlive for the rationale.
        [NativeDisableUnsafePtrRestriction]
        readonly NativeHeapEntry* _slot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            TrecsArrayWrite<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsArrayWrite(
            T* data,
            int length,
            NativeHeapEntry* slot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _data = data;
            _length = length;
            _slot = slot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
            if (slot != null)
            {
                CollectionHelper.SetStaticSafetyId<TrecsArrayWrite<T>>(
                    ref m_Safety,
                    ref s_staticSafetyId.Data
                );
            }
            CheckSlotAlive();
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsArrayWrite(T* data, int length, NativeHeapEntry* slot, byte capturedGeneration)
        {
            _data = data;
            _length = length;
            _slot = slot;
            _capturedGeneration = capturedGeneration;
            CheckSlotAlive();
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            // See TrecsArrayRead<T>.CheckSlotAlive for the null-slot rationale.
            if (_slot == null)
            {
                return;
            }
            TrecsAssert.That(
                _slot->Generation == _capturedGeneration && _slot->InUse == 1,
                "TrecsArrayWrite is stale: the underlying TrecsArray allocation has been "
                    + "freed since this wrapper was opened (captured slot gen {0}, current {1}, "
                    + "InUse {2}).",
                _capturedGeneration,
                _slot->Generation,
                _slot->InUse
            );
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (_slot != null)
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                }
#endif
                return _length;
            }
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (_slot != null)
                {
                    AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                }
#endif
                TrecsDebugAssert.That(
                    (uint)index < (uint)_length,
                    "TrecsArrayWrite index {0} out of range (Length={1})",
                    index,
                    _length
                );
                return ref _data[index];
            }
        }
    }
}
