using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Burst-safe read-only view over a <see cref="TrecsList{T}"/>. Obtain via
    /// <see cref="TrecsList{T}.Read(in NativeWorldAccessor)"/> or
    /// <see cref="TrecsList{T}.Read(in NativeChunkStoreResolver)"/> inside a job. Many
    /// readers may hold this concurrently without conflict because the wrapper is
    /// <c>[NativeContainerIsReadOnly]</c>; a concurrent <see cref="NativeTrecsListWrite{T}"/>
    /// over the same list is rejected at <c>Schedule</c> time.
    ///
    /// <para>The wrapper caches the data buffer pointer at Open time so element access is
    /// a direct <c>_data[index]</c> with no per-access indirection. Because this view
    /// cannot grow the list itself, the cached pointer is stable for the wrapper's
    /// lifetime — any reallocation by a concurrent writer is blocked by the safety walker
    /// at schedule time.</para>
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> See
    /// <see cref="TrecsListRead{T}"/> for the rationale.</para>
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeTrecsListRead<T>
        where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly TrecsListHeader* _header;

        [NativeDisableUnsafePtrRestriction]
        readonly T* _data;

        readonly ushort _capturedVersion;

        // See TrecsListRead<T> for the shipping-build use-after-dispose guard.
        [NativeDisableUnsafePtrRestriction]
        readonly NativeChunkStoreEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeTrecsListRead<T>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsListRead(
            TrecsListHeader* header,
            T* data,
            NativeChunkStoreEntry* headerSlot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _header = header;
            _data = data;
            _capturedVersion = header->Version;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeTrecsListRead<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsListRead(
            TrecsListHeader* header,
            T* data,
            NativeChunkStoreEntry* headerSlot,
            byte capturedGeneration
        )
        {
            _header = header;
            _data = data;
            _capturedVersion = header->Version;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            TrecsAssert.That(
                _headerSlot->Generation == _capturedGeneration && _headerSlot->InUse == 1,
                "NativeTrecsListRead is stale: the underlying TrecsList allocation has been "
                    + "freed since this wrapper was opened (captured slot gen {0}, current {1}, "
                    + "InUse {2}).",
                _capturedGeneration,
                _headerSlot->Generation,
                _headerSlot->InUse
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckVersion()
        {
            TrecsAssert.That(
                _header->Version == _capturedVersion,
                "NativeTrecsListRead is stale: the list's data buffer was reallocated since "
                    + "this wrapper was opened (captured version {0}, current {1}).",
                _capturedVersion,
                _header->Version
            );
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                CheckSlotAlive();
                CheckVersion();
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
                CheckSlotAlive();
                CheckVersion();
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
                CheckSlotAlive();
                CheckVersion();
                TrecsDebugAssert.That(
                    (uint)index < (uint)_header->Count,
                    "NativeTrecsListRead index {0} out of range (Count={1})",
                    index,
                    _header->Count
                );
                return ref _data[index];
            }
        }

        /// <summary>
        /// Enables <c>foreach (var x in r)</c> inside Burst jobs. Same safety story
        /// as the managed enumerator: every iteration goes through the indexer,
        /// version-checks, and bounds-checks. A sibling mutation that bumps the
        /// version throws on the next <c>MoveNext</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeTrecsListReadEnumerator<T> GetEnumerator() =>
            new NativeTrecsListReadEnumerator<T>(this);
    }

    public struct NativeTrecsListReadEnumerator<T>
        where T : unmanaged
    {
        NativeTrecsListRead<T> _view;
        int _index;

        public NativeTrecsListReadEnumerator(NativeTrecsListRead<T> view)
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
            return _index < _view.Count;
        }
    }
}
