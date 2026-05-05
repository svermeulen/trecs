using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs.Internal // not part of public api atm
{
    /// <summary>
    /// A growable double-ended queue (deque) backed by a circular buffer.
    /// Burst-compatible. Single-threaded.
    /// </summary>
    /// <remarks>
    /// Unlike <c>UnsafeRingQueue&lt;T&gt;</c>, this container grows on overflow and supports
    /// push/pop at both ends plus indexed access. It is the unsafe core that
    /// <c>NativeRingDeque&lt;T&gt;</c> wraps with safety handles.
    /// </remarks>
    /// <typeparam name="T">The type of the elements.</typeparam>
    [DebuggerDisplay(
        "Length = {Length}, Capacity = {Capacity}, IsCreated = {IsCreated}, IsEmpty = {IsEmpty}"
    )]
    [DebuggerTypeProxy(typeof(UnsafeRingDequeDebugView<>))]
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct UnsafeRingDeque<T> : INativeDisposable
        where T : unmanaged
    {
        public const int DefaultCapacity = 16;

        [NativeDisableUnsafePtrRestriction]
        public T* Ptr;

        public AllocatorManager.AllocatorHandle Allocator;

        internal int m_Capacity;
        internal int m_Length;

        // Index of the first element. Equal to m_Back when the deque is empty or full.
        internal int m_Front;

        // Index one past the last element (wraps).
        internal int m_Back;

        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_Length == 0;
        }

        public readonly int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_Length;
        }

        public readonly int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_Capacity;
        }

        public readonly bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Ptr != null;
        }

        /// <summary>
        /// Initializes and returns an instance of UnsafeRingDeque.
        /// </summary>
        /// <param name="capacity">The initial capacity (must be >= 1).</param>
        /// <param name="allocator">The allocator to use.</param>
        /// <param name="options">Whether newly allocated bytes should be zeroed out.</param>
        public UnsafeRingDeque(
            int capacity,
            AllocatorManager.AllocatorHandle allocator,
            NativeArrayOptions options = NativeArrayOptions.ClearMemory
        )
        {
            CheckCapacity(capacity);

            Allocator = allocator;
            m_Capacity = capacity;
            m_Length = 0;
            m_Front = 0;
            m_Back = 0;

            var sizeOf = UnsafeUtility.SizeOf<T>();
            Ptr = (T*)
                AllocatorManager.Allocate(allocator, sizeOf, UnsafeUtility.AlignOf<T>(), capacity);

            if (options == NativeArrayOptions.ClearMemory)
            {
                UnsafeUtility.MemClear(Ptr, (long)capacity * sizeOf);
            }
        }

        internal static UnsafeRingDeque<T>* Alloc(AllocatorManager.AllocatorHandle allocator)
        {
            return (UnsafeRingDeque<T>*)
                AllocatorManager.Allocate(
                    allocator,
                    sizeof(UnsafeRingDeque<T>),
                    UnsafeUtility.AlignOf<UnsafeRingDeque<T>>(),
                    items: 1
                );
        }

        internal static void Free(UnsafeRingDeque<T>* data)
        {
            if (data == null)
            {
                throw new InvalidOperationException(
                    "UnsafeRingDeque has yet to be created or has been destroyed!"
                );
            }
            var allocator = data->Allocator;
            data->Dispose();
            AllocatorManager.Free(
                allocator,
                data,
                sizeof(UnsafeRingDeque<T>),
                UnsafeUtility.AlignOf<UnsafeRingDeque<T>>(),
                items: 1
            );
        }

        /// <summary>
        /// Releases all resources (memory).
        /// </summary>
        public void Dispose()
        {
            if (!IsCreated)
            {
                return;
            }

            // Allocator.None means the buffer is externally owned; only free if we allocated it.
            if (Allocator.ToAllocator > Unity.Collections.Allocator.None)
            {
                AllocatorManager.Free(
                    Allocator,
                    Ptr,
                    UnsafeUtility.SizeOf<T>(),
                    UnsafeUtility.AlignOf<T>(),
                    m_Capacity
                );
                Allocator = AllocatorManager.Invalid;
            }

            Ptr = null;
        }

        /// <summary>
        /// Creates and schedules a job that will dispose this deque.
        /// </summary>
        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (!IsCreated)
            {
                return inputDeps;
            }

            if (Allocator.ToAllocator > Unity.Collections.Allocator.None)
            {
                var jobHandle = new BufferDisposeJob
                {
                    Ptr = Ptr,
                    Allocator = Allocator,
                    SizeOf = UnsafeUtility.SizeOf<T>(),
                    AlignOf = UnsafeUtility.AlignOf<T>(),
                    Items = m_Capacity,
                }.Schedule(inputDeps);

                Ptr = null;
                Allocator = AllocatorManager.Invalid;

                return jobHandle;
            }

            Ptr = null;
            return inputDeps;
        }

        /// <summary>
        /// Adds an element at the back of the deque, growing capacity if full.
        /// </summary>
        public void PushBack(in T value)
        {
            if (m_Length == m_Capacity)
            {
                Grow(m_Capacity * 2);
            }
            Ptr[m_Back] = value;
            m_Back++;
            if (m_Back == m_Capacity)
                m_Back = 0;
            m_Length++;
        }

        /// <summary>
        /// Adds an element at the front of the deque, growing capacity if full.
        /// </summary>
        public void PushFront(in T value)
        {
            if (m_Length == m_Capacity)
            {
                Grow(m_Capacity * 2);
            }
            if (m_Front == 0)
                m_Front = m_Capacity;
            m_Front--;
            Ptr[m_Front] = value;
            m_Length++;
        }

        /// <summary>
        /// Removes and returns the element at the front of the deque.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the deque is empty.</exception>
        public T PopFront()
        {
            if (!TryPopFront(out var item))
            {
                ThrowEmpty();
            }
            return item;
        }

        /// <summary>
        /// Removes and returns the element at the front of the deque, or false if empty.
        /// </summary>
        public bool TryPopFront(out T item)
        {
            if (m_Length == 0)
            {
                item = default;
                return false;
            }
            item = Ptr[m_Front];
            m_Front++;
            if (m_Front == m_Capacity)
                m_Front = 0;
            m_Length--;
            return true;
        }

        /// <summary>
        /// Removes and returns the element at the back of the deque.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the deque is empty.</exception>
        public T PopBack()
        {
            if (!TryPopBack(out var item))
            {
                ThrowEmpty();
            }
            return item;
        }

        /// <summary>
        /// Removes and returns the element at the back of the deque, or false if empty.
        /// </summary>
        public bool TryPopBack(out T item)
        {
            if (m_Length == 0)
            {
                item = default;
                return false;
            }
            if (m_Back == 0)
                m_Back = m_Capacity;
            m_Back--;
            item = Ptr[m_Back];
            m_Length--;
            return true;
        }

        /// <summary>
        /// Returns the element at the front of the deque without removing it.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the deque is empty.</exception>
        public readonly T PeekFront()
        {
            if (!TryPeekFront(out var item))
            {
                ThrowEmpty();
            }
            return item;
        }

        public readonly bool TryPeekFront(out T item)
        {
            if (m_Length == 0)
            {
                item = default;
                return false;
            }
            item = Ptr[m_Front];
            return true;
        }

        /// <summary>
        /// Returns the element at the back of the deque without removing it.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the deque is empty.</exception>
        public readonly T PeekBack()
        {
            if (!TryPeekBack(out var item))
            {
                ThrowEmpty();
            }
            return item;
        }

        public readonly bool TryPeekBack(out T item)
        {
            if (m_Length == 0)
            {
                item = default;
                return false;
            }
            var idx = m_Back == 0 ? m_Capacity - 1 : m_Back - 1;
            item = Ptr[idx];
            return true;
        }

        /// <summary>
        /// Indexed access in front-to-back logical order. Index 0 is the front element.
        /// </summary>
        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckIndex(index);
                var idx = m_Front + index;
                if (idx >= m_Capacity)
                    idx -= m_Capacity;
                return ref Ptr[idx];
            }
        }

        /// <summary>
        /// Removes all elements. Capacity is unchanged. Element memory is not zeroed.
        /// </summary>
        public void Clear()
        {
            m_Length = 0;
            m_Front = 0;
            m_Back = 0;
        }

        /// <summary>
        /// Grows capacity to at least <paramref name="minCapacity"/>. No-op if already large enough.
        /// Never shrinks.
        /// </summary>
        public void EnsureCapacity(int minCapacity)
        {
            if (minCapacity <= m_Capacity)
            {
                return;
            }
            var newCapacity = Math.Max(minCapacity, m_Capacity * 2);
            Grow(newCapacity);
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        void Grow(int newCapacity)
        {
            var sizeOf = UnsafeUtility.SizeOf<T>();
            var newPtr = (T*)
                AllocatorManager.Allocate(
                    Allocator,
                    sizeOf,
                    UnsafeUtility.AlignOf<T>(),
                    newCapacity
                );

            if (m_Length > 0)
            {
                if (m_Front < m_Back)
                {
                    UnsafeUtility.MemCpy(newPtr, Ptr + m_Front, (long)m_Length * sizeOf);
                }
                else
                {
                    var firstPart = m_Capacity - m_Front;
                    UnsafeUtility.MemCpy(newPtr, Ptr + m_Front, (long)firstPart * sizeOf);
                    UnsafeUtility.MemCpy(newPtr + firstPart, Ptr, (long)m_Back * sizeOf);
                }
            }

            AllocatorManager.Free(Allocator, Ptr, sizeOf, UnsafeUtility.AlignOf<T>(), m_Capacity);

            Ptr = newPtr;
            m_Capacity = newCapacity;
            m_Front = 0;
            m_Back = m_Length;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_DOTS_DEBUG")]
        static void CheckCapacity(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentException(
                    $"Capacity must be at least 1, was {capacity}",
                    nameof(capacity)
                );
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_DOTS_DEBUG")]
        readonly void CheckIndex(int index)
        {
            if ((uint)index >= (uint)m_Length)
            {
                throw new IndexOutOfRangeException(
                    $"Index {index} is out of range [0, {m_Length})"
                );
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_DOTS_DEBUG")]
        static void ThrowEmpty()
        {
            throw new InvalidOperationException("Deque is empty");
        }

        [BurstCompile]
        struct BufferDisposeJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public T* Ptr;
            public AllocatorManager.AllocatorHandle Allocator;
            public int SizeOf;
            public int AlignOf;
            public int Items;

            public void Execute()
            {
                AllocatorManager.Free(Allocator, Ptr, SizeOf, AlignOf, Items);
            }
        }

        public struct Enumerator
        {
            UnsafeRingDeque<T> _deque;
            int _index;

            internal Enumerator(in UnsafeRingDeque<T> deque)
            {
                _deque = deque;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _deque.m_Length;

            public ref T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _deque[_index];
            }

            public void Reset() => _index = -1;
        }
    }

    internal sealed class UnsafeRingDequeDebugView<T>
        where T : unmanaged
    {
        UnsafeRingDeque<T> _data;

        public UnsafeRingDequeDebugView(UnsafeRingDeque<T> data)
        {
            _data = data;
        }

        public unsafe T[] Items
        {
            get
            {
                var result = new T[_data.Length];
                for (var i = 0; i < result.Length; ++i)
                {
                    result[i] = _data[i];
                }
                return result;
            }
        }
    }
}
