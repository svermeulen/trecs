using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Burst-safe read-only view over a <see cref="TrecsArray{T}"/>. Obtain via
    /// <see cref="TrecsArray{T}.Read(WorldAccessor)"/>,
    /// <see cref="TrecsArray{T}.Read(WorldAccessor)"/>,
    /// <see cref="TrecsArray{T}.Read(in NativeWorldAccessor)"/>, or
    /// <see cref="TrecsArray{T}.Read(in NativeHeapResolver)"/>. Works both on
    /// the main thread and inside <c>[BurstCompile]</c> jobs. Many readers may hold
    /// this concurrently without conflict because the wrapper is
    /// <c>[NativeContainerIsReadOnly]</c>; a concurrent <see cref="TrecsArrayWrite{T}"/>
    /// over the same array is rejected at <c>Schedule</c> time.
    ///
    /// <para>The wrapper caches the data buffer pointer and length at Open time so
    /// element access is a direct <c>_data[index]</c> with no per-access indirection.
    /// Arrays don't grow, so the cached pointer is stable for the wrapper's lifetime
    /// — no version stamp is needed.</para>
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> The wrapper captures the
    /// data slot's <c>NativeHeapEntry.Generation</c> at construction and validates
    /// it once. Matching NativeList/NativeArray semantics, per-access checks are
    /// editor-only (via <c>AtomicSafetyHandle</c>); the job safety walker catches
    /// cross-job conflicts at schedule time.</para>
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct TrecsArrayRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly T* _data;

        readonly int _length;

        // See class XML doc for the shipping-build use-after-dispose guard. For the
        // Length=0 / null-handle path, _slot is null and CheckSlotAlive is skipped —
        // the bounds check on Length=0 trips every indexed access before the data
        // pointer is dereferenced.
        [NativeDisableUnsafePtrRestriction]
        readonly NativeHeapEntry* _slot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            TrecsArrayRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsArrayRead(
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
                CollectionHelper.SetStaticSafetyId<TrecsArrayRead<T>>(
                    ref m_Safety,
                    ref s_staticSafetyId.Data
                );
            }
            CheckSlotAlive();
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsArrayRead(T* data, int length, NativeHeapEntry* slot, byte capturedGeneration)
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
            // Length=0 / null-handle wrappers carry _slot == null — there is no
            // slot to check. Indexed access still trips the bounds check before
            // touching _data.
            if (_slot == null)
            {
                return;
            }
            TrecsAssert.That(
                _slot->Generation == _capturedGeneration && _slot->InUse == 1,
                "TrecsArrayRead is stale: the underlying TrecsArray allocation has been "
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

        public ref readonly T this[int index]
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
                TrecsDebugAssert.That(
                    (uint)index < (uint)_length,
                    "TrecsArrayRead index {0} out of range (Length={1})",
                    index,
                    _length
                );
                return ref _data[index];
            }
        }

        /// <summary>
        /// Enables <c>foreach (var x in r)</c> over the read view. Each element
        /// access goes through the wrapper's indexer, so the safety check fires on
        /// every iteration — opening a write wrapper and disposing the array mid-
        /// iteration is caught (in editor) by the safety walker.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TrecsArrayReadEnumerator<T> GetEnumerator() => new TrecsArrayReadEnumerator<T>(this);
    }

    public struct TrecsArrayReadEnumerator<T>
        where T : unmanaged
    {
        TrecsArrayRead<T> _view;
        int _index;

        public TrecsArrayReadEnumerator(TrecsArrayRead<T> view)
        {
            _view = view;
            _index = -1;
        }

        public ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _view[_index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            _index++;
            return _index < _view.Length;
        }
    }
}
