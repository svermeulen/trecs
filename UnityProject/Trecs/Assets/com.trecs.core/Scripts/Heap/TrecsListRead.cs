using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Main-thread read-only view over a <see cref="TrecsList{T}"/>. Obtain via
    /// <see cref="TrecsList{T}.Read(WorldAccessor)"/> or
    /// <see cref="TrecsList{T}.Read(WorldAccessor)"/>. For in-job access use the
    /// Burst-safe <see cref="NativeTrecsListRead{T}"/> instead.
    ///
    /// <para>Declared as a <c>ref struct</c> so the view is stack-bound: it cannot be
    /// captured by a lambda, stored as a field, or passed across an <c>async</c>
    /// boundary. That narrows the view's lifetime to a single method scope and
    /// rules out a class of "wrapper outlived its underlying allocation" foot-guns
    /// — most importantly, holding a read view across an <c>EnsureCapacity</c>
    /// reallocation.</para>
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> In addition to the
    /// editor-only <c>AtomicSafetyHandle</c> check, the wrapper captures the
    /// header slot's <c>NativeHeapEntry.Generation</c> at Open and
    /// re-checks it on every Read. If the underlying allocation was freed and
    /// its side-table slot recycled, the check fires (1-byte generation, so
    /// roughly 1/256 chance of coincidental match after the slot is reused).
    /// This check runs unconditionally — gives a non-trivial shipping-build
    /// safety net where Unity's safety handle is compiled out.</para>
    /// </summary>
    public readonly unsafe ref struct TrecsListRead<T>
        where T : unmanaged
    {
        readonly TrecsListHeader* _header;
        readonly T* _data;
        readonly ushort _capturedVersion;

        // Side-table slot pointer for the header allocation. Stable across the
        // chunk store's lifetime (chunks never move). Used to re-check the
        // slot's Generation byte on every access so stale wrappers fail loudly
        // in shipping builds where AtomicSafetyHandle is compiled out.
        readonly NativeHeapEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsListRead(
            TrecsListHeader* header,
            T* data,
            NativeHeapEntry* headerSlot,
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
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsListRead(
            TrecsListHeader* header,
            T* data,
            NativeHeapEntry* headerSlot,
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
            // Shipping-build use-after-dispose guard. _headerSlot points at the
            // side-table entry (stable address); reading its Generation/InUse
            // detects whether the slot was freed and recycled since this
            // wrapper was opened. 1-byte generation wraps after 256 cycles, so
            // an unlucky stale wrapper has a 1/256 chance of seeing a matching
            // generation on a recycled slot — accepted as the size/perf
            // trade-off for not adding a second wider counter.
            TrecsAssert.That(
                _headerSlot->Generation == _capturedGeneration && _headerSlot->InUse == 1,
                "TrecsListRead is stale: the underlying TrecsList allocation has been "
                    + "freed since this wrapper was opened (captured slot gen {0}, "
                    + "current {1}, InUse {2}). Re-open the wrapper after any code path "
                    + "that disposes the list.",
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
                "TrecsListRead is stale: the list's data buffer was reallocated since this "
                    + "wrapper was opened (captured version {0}, current {1}). Re-open the "
                    + "wrapper after any code path that grows the list.",
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
                    "TrecsListRead index {0} out of range (Count={1})",
                    index,
                    _header->Count
                );
                return ref _data[index];
            }
        }

        /// <summary>
        /// Enables <c>foreach (var x in r)</c> over the read view. Each element
        /// access goes through the wrapper's indexer, so version + safety checks
        /// fire on every iteration — a sibling mutation that bumps the version
        /// throws on the next <c>MoveNext</c> instead of returning stale data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TrecsListReadEnumerator<T> GetEnumerator() => new TrecsListReadEnumerator<T>(this);
    }

    public ref struct TrecsListReadEnumerator<T>
        where T : unmanaged
    {
        TrecsListRead<T> _view;
        int _index;

        public TrecsListReadEnumerator(TrecsListRead<T> view)
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
