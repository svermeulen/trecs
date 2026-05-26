using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Main-thread writable view over a <see cref="TrecsList{T}"/>. Supports indexed
    /// writes, <c>Add</c> (auto-grows), <c>RemoveAt</c>, <c>Clear</c>, and
    /// <see cref="EnsureCapacity"/>. For in-job access use the Burst-safe
    /// <see cref="NativeTrecsListWrite{T}"/> instead — it does not auto-grow and
    /// requires the list to be pre-sized before scheduling.
    ///
    /// <para>Declared as a <c>ref struct</c> so the view is stack-bound: it cannot
    /// be captured by a lambda, stored as a field, or passed across an <c>async</c>
    /// boundary. That narrows the view's lifetime to a single method scope and
    /// rules out a class of "wrapper outlived its underlying allocation" foot-guns.
    /// In-place self-grow (<see cref="Add"/>/<see cref="EnsureCapacity"/>) updates
    /// the cached data pointer on this wrapper, so the cached pointer is never
    /// stale within the wrapper's scope.</para>
    ///
    /// <para><b>Shipping-build use-after-dispose guard.</b> See
    /// <see cref="TrecsListRead{T}"/> for the rationale — the wrapper captures
    /// the header slot's <c>Generation</c> at Open and re-checks on every
    /// op, gated only by the chunk-store side-table generation byte (not by
    /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>).</para>
    /// </summary>
    public unsafe ref struct TrecsListWrite<T>
        where T : unmanaged
    {
        readonly TrecsListHeader* _header;
        T* _data;
        readonly NativeHeap _store;
        ushort _capturedVersion;

        // See TrecsListRead<T> for the shipping-build use-after-dispose guard.
        readonly NativeHeapEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal TrecsListWrite(
            TrecsListHeader* header,
            T* data,
            NativeHeap store,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _header = header;
            _data = data;
            _store = store;
            _capturedVersion = header->Version;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal TrecsListWrite(
            TrecsListHeader* header,
            T* data,
            NativeHeap store,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration
        )
        {
            _header = header;
            _data = data;
            _store = store;
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
                "TrecsListWrite is stale: the underlying TrecsList allocation has been "
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
                "TrecsListWrite is stale: the list was mutated through another path since "
                    + "this wrapper was opened (captured version {0}, current {1}). Any "
                    + "Add/Remove/Clear/EnsureCapacity on a sibling wrapper or via the "
                    + "handle invalidates other wrappers, matching List<T> enumerator "
                    + "semantics. Re-open the wrapper after the mutation.",
                _capturedVersion,
                _header->Version
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void BumpVersionAndResync()
        {
            unchecked
            {
                _capturedVersion = ++_header->Version;
            }
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

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                CheckSlotAlive();
                CheckVersion();
                TrecsDebugAssert.That(
                    (uint)index < (uint)_header->Count,
                    "TrecsListWrite index {0} out of range (Count={1})",
                    index,
                    _header->Count
                );
                return ref _data[index];
            }
        }

        /// <summary>
        /// Appends <paramref name="value"/>. Auto-grows the backing buffer when
        /// <c>Count == Capacity</c> (doubling, or starting at 4 on first allocation);
        /// the cached data pointer on this wrapper is updated in place so subsequent
        /// access stays valid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in T value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
            var count = _header->Count;
            if (count == _header->Capacity)
            {
                Grow(
                    TrecsList.ComputeNewCapacity(_header->Capacity, count + 1, _header->ElementSize)
                );
            }
            _data[count] = value;
            _header->Count = count + 1;
            BumpVersionAndResync();
        }

        /// <summary>
        /// Grows the backing buffer so it can hold at least <paramref name="minCapacity"/>
        /// elements without further reallocation. Doubles geometrically past the existing
        /// capacity. The cached data pointer on this wrapper is updated in place.
        /// </summary>
        public void EnsureCapacity(
            int minCapacity,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
            TrecsDebugAssert.That(minCapacity >= 0, "minCapacity must be non-negative");
            if (minCapacity <= _header->Capacity)
            {
                return;
            }
            var newCapacity = TrecsList.ComputeNewCapacity(
                _header->Capacity,
                minCapacity,
                _header->ElementSize
            );
            Grow(newCapacity, callerFile, callerLine);
            BumpVersionAndResync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
            _header->Count = 0;
            BumpVersionAndResync();
        }

        /// <summary>Removes element at <paramref name="index"/>, shifting later elements down (O(N)).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsListWrite.RemoveAt index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            var data = _data;
            var tail = _header->Count - index - 1;
            if (tail > 0)
            {
                UnsafeUtility.MemMove(data + index, data + index + 1, (long)tail * sizeof(T));
            }
            _header->Count -= 1;
            BumpVersionAndResync();
        }

        /// <summary>
        /// Removes the element at <paramref name="index"/> by swapping the last element into
        /// its slot (O(1), does not preserve order).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAtSwapBack(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsListWrite.RemoveAtSwapBack index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            var data = _data;
            var last = _header->Count - 1;
            if (index != last)
            {
                data[index] = data[last];
            }
            _header->Count -= 1;
            BumpVersionAndResync();
        }

        // Allocates a fresh data slot at the requested capacity, copies live elements
        // across, updates the header (DataHandle + Capacity), and rebinds this wrapper's
        // cached _data pointer in place. Frees the previous data slot last so the
        // chunk store's safety check can run on the old slot — no wrapper carries the
        // data slot's safety handle, so the check passes vacuously.
        void Grow(int newCapacity, string callerFile = null, int callerLine = 0)
        {
            // Auto-grow is unconditional now that Write itself rejects non-mutating
            // accessors at Open time — any TrecsListWrite that exists was opened
            // from a Fixed-role or Unrestricted-role accessor and is allowed to
            // allocate a fresh data slot.
            var oldDataHandle = _header->DataHandle;
            var elementSize = _header->ElementSize;
            var liveBytes = (long)_header->Count * elementSize;
            var newByteSize = TrecsList.ByteSizeOrThrow(newCapacity, elementSize);

            var newHandle = _store.Alloc(
                newByteSize,
                _header->ElementAlign,
                TypeId<TrecsListDataMarker<T>>.Value.Value,
                out var newAddress,
                callerFile,
                callerLine
            );

            if (liveBytes > 0)
            {
                var oldEntry = _store.ResolveEntry(oldDataHandle);
                UnsafeUtility.MemCpy(
                    newAddress.ToPointer(),
                    oldEntry.Address.ToPointer(),
                    liveBytes
                );
            }

            _header->DataHandle = newHandle;
            _header->Capacity = newCapacity;
            _data = (T*)newAddress.ToPointer();
            // No BumpVersionAndResync here — the calling mutation (Add / EnsureCapacity)
            // bumps once at the end. One logical op = one bump.

            if (!oldDataHandle.IsNull)
            {
                _store.Free(oldDataHandle);
            }
        }

        /// <summary>
        /// Sorts the list in place using <paramref name="comparer"/> (O(N log N)).
        /// Bumps the version, so any sibling wrapper or in-flight enumerator throws
        /// on next access — matches <see cref="System.Collections.Generic.List{T}"/>
        /// sort semantics. Runs synchronously on the calling thread; for large
        /// lists where worker-thread overlap is worth the scheduling overhead,
        /// chain a separate sort job into your existing graph instead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Sort<TComparer>(TComparer comparer)
            where TComparer : IComparer<T>
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
            NativeSortExtension.Sort(_data, _header->Count, comparer);
            BumpVersionAndResync();
        }

        /// <summary>
        /// Enables <c>foreach (ref var x in w)</c> over the write view. Each element
        /// access goes through the wrapper's indexer, so version + safety checks fire
        /// on every iteration — a sibling mutation that bumps the version (Add /
        /// Remove / Clear / EnsureCapacity, on this wrapper or another) throws on the
        /// next <c>MoveNext</c>, matching <see cref="System.Collections.Generic.List{T}"/>
        /// enumerator semantics. <c>Current</c> returns <c>ref T</c> so elements can be
        /// mutated in place; structurally mutating the list during iteration is not
        /// allowed and will throw.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TrecsListWriteEnumerator<T> GetEnumerator() => new TrecsListWriteEnumerator<T>(this);
    }

    public ref struct TrecsListWriteEnumerator<T>
        where T : unmanaged
    {
        TrecsListWrite<T> _view;
        int _index;

        public TrecsListWriteEnumerator(TrecsListWrite<T> view)
        {
            _view = view;
            _index = -1;
        }

        public ref T Current
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

    /// <summary>
    /// Default-ordering sort for <see cref="TrecsListWrite{T}"/>. Lives as an
    /// extension method (rather than a member) because the wrapper's
    /// <c>where T : unmanaged</c> can't be tightened to add
    /// <see cref="System.IComparable{T}"/> on a single member — mirrors how
    /// <c>NativeSortExtension.Sort</c> is structured for <c>NativeList&lt;T&gt;</c>.
    /// Routes through the member <see cref="TrecsListWrite{T}.Sort{TComparer}"/>
    /// with a struct comparer so all the same safety + version checks fire.
    /// </summary>
    public static class TrecsListWriteSortExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Sort<T>(this ref TrecsListWrite<T> list)
            where T : unmanaged, IComparable<T>
        {
            list.Sort(default(DefaultComparer<T>));
        }

        struct DefaultComparer<T> : IComparer<T>
            where T : IComparable<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(T x, T y) => x.CompareTo(y);
        }
    }
}
