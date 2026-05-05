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
    /// Index 0 is the front element. PushBack/PushFront grow capacity (doubling) when full.
    /// PopFront/PopBack and the indexer all run in O(1).
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    [DebuggerDisplay(
        "Length = {Length}, Capacity = {Capacity}, IsCreated = {IsCreated}, IsEmpty = {IsEmpty}"
    )]
    [DebuggerTypeProxy(typeof(NativeRingDequeDebugView<>))]
    public unsafe struct NativeRingDeque<T> : INativeDisposable
        where T : unmanaged
    {
        public const int DefaultCapacity = UnsafeRingDeque<T>.DefaultCapacity;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;
        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeRingDeque<T>
        >();
#endif

        [NativeDisableUnsafePtrRestriction]
        internal UnsafeRingDeque<T>* m_Deque;

        public NativeRingDeque(
            int capacity,
            AllocatorManager.AllocatorHandle allocator,
            NativeArrayOptions options = NativeArrayOptions.ClearMemory
        )
        {
            CheckAllocator(allocator);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = CollectionHelper.CreateSafetyHandle(allocator);
            CollectionHelper.SetStaticSafetyId<NativeRingDeque<T>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
#endif
            m_Deque = UnsafeRingDeque<T>.Alloc(allocator);
            *m_Deque = new UnsafeRingDeque<T>(capacity, allocator, options);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS"), Conditional("UNITY_DOTS_DEBUG")]
        static void CheckAllocator(AllocatorManager.AllocatorHandle allocator)
        {
            if (allocator.ToAllocator <= Allocator.None)
            {
                throw new ArgumentException($"Allocator {allocator} must not be None or Invalid");
            }
        }

        public readonly bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_Deque != null && m_Deque->IsCreated;
        }

        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => m_Deque == null || m_Deque->IsEmpty;
        }

        public readonly int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckRead();
                return m_Deque->Length;
            }
        }

        public readonly int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckRead();
                return m_Deque->Capacity;
            }
        }

        /// <summary>
        /// Adds an element at the back of the deque, growing capacity if full.
        /// </summary>
        public void PushBack(in T value)
        {
            CheckWrite();
            m_Deque->PushBack(value);
        }

        /// <summary>
        /// Adds an element at the front of the deque, growing capacity if full.
        /// </summary>
        public void PushFront(in T value)
        {
            CheckWrite();
            m_Deque->PushFront(value);
        }

        public T PopFront()
        {
            CheckWrite();
            return m_Deque->PopFront();
        }

        public bool TryPopFront(out T item)
        {
            CheckWrite();
            return m_Deque->TryPopFront(out item);
        }

        public T PopBack()
        {
            CheckWrite();
            return m_Deque->PopBack();
        }

        public bool TryPopBack(out T item)
        {
            CheckWrite();
            return m_Deque->TryPopBack(out item);
        }

        public readonly T PeekFront()
        {
            CheckRead();
            return m_Deque->PeekFront();
        }

        public readonly bool TryPeekFront(out T item)
        {
            CheckRead();
            return m_Deque->TryPeekFront(out item);
        }

        public readonly T PeekBack()
        {
            CheckRead();
            return m_Deque->PeekBack();
        }

        public readonly bool TryPeekBack(out T item)
        {
            CheckRead();
            return m_Deque->TryPeekBack(out item);
        }

        /// <summary>
        /// Indexed access in front-to-back logical order. Index 0 is the front element.
        /// </summary>
        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckRead();
                return (*m_Deque)[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                CheckWrite();
                (*m_Deque)[index] = value;
            }
        }

        /// <summary>
        /// Returns a writable reference to the element at the given logical index.
        /// </summary>
        public ref T ElementAt(int index)
        {
            CheckWrite();
            return ref (*m_Deque)[index];
        }

        public void Clear()
        {
            CheckWrite();
            m_Deque->Clear();
        }

        public void EnsureCapacity(int minCapacity)
        {
            CheckWrite();
            m_Deque->EnsureCapacity(minCapacity);
        }

        public readonly Enumerator GetEnumerator()
        {
            CheckRead();
            return new Enumerator(m_Deque);
        }

        public void Dispose()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!AtomicSafetyHandle.IsDefaultValue(m_Safety))
            {
                AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            }
#endif
            if (!IsCreated)
            {
                return;
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            CollectionHelper.DisposeSafetyHandle(ref m_Safety);
#endif
            UnsafeRingDeque<T>.Free(m_Deque);
            m_Deque = null;
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (!AtomicSafetyHandle.IsDefaultValue(m_Safety))
            {
                AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            }
#endif
            if (!IsCreated)
            {
                return inputDeps;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var jobHandle = new NativeRingDequeDisposeJob
            {
                Data = new NativeRingDequeDispose
                {
                    m_DequeData = (UnsafeRingDeque<int>*)m_Deque,
                    m_Safety = m_Safety,
                },
            }.Schedule(inputDeps);
            AtomicSafetyHandle.Release(m_Safety);
#else
            var jobHandle = new NativeRingDequeDisposeJob
            {
                Data = new NativeRingDequeDispose { m_DequeData = (UnsafeRingDeque<int>*)m_Deque },
            }.Schedule(inputDeps);
#endif
            m_Deque = null;
            return jobHandle;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckWrite()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
        }

        public struct Enumerator
        {
            [NativeDisableUnsafePtrRestriction]
            UnsafeRingDeque<T>* _deque;
            int _index;

            internal Enumerator(UnsafeRingDeque<T>* deque)
            {
                _deque = deque;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _deque->Length;

            public ref T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref (*_deque)[_index];
            }

            public void Reset() => _index = -1;
        }
    }

    internal sealed class NativeRingDequeDebugView<T>
        where T : unmanaged
    {
        readonly unsafe UnsafeRingDeque<T>* _data;

        public unsafe NativeRingDequeDebugView(NativeRingDeque<T> data)
        {
            _data = data.m_Deque;
        }

        public unsafe T[] Items
        {
            get
            {
                if (_data == null || !_data->IsCreated)
                {
                    return Array.Empty<T>();
                }
                var result = new T[_data->Length];
                for (var i = 0; i < result.Length; ++i)
                {
                    result[i] = (*_data)[i];
                }
                return result;
            }
        }
    }

    [NativeContainer]
    internal unsafe struct NativeRingDequeDispose
    {
        [NativeDisableUnsafePtrRestriction]
        public UnsafeRingDeque<int>* m_DequeData;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public AtomicSafetyHandle m_Safety;
#endif

        public void Dispose()
        {
            UnsafeRingDeque<int>.Free(m_DequeData);
        }
    }

    [BurstCompile]
    internal unsafe struct NativeRingDequeDisposeJob : IJob
    {
        public NativeRingDequeDispose Data;

        public void Execute()
        {
            Data.Dispose();
        }
    }
}
