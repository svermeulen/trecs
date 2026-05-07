#if TRECS_INTERNAL_CHECKS && DEBUG
#define ENABLE_DEBUG_CHECKS
#endif

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Burst-friendly typeless ring-buffer queue with mixed-type Enqueue/Dequeue,
    /// reservation slots, and growth on demand. Stored values can be any unmanaged
    /// type — callers are responsible for dequeuing types in the same order they
    /// were enqueued. The struct is copyable; copies share the underlying buffer.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NativeBag : IDisposable
    {
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    BasicTests();
                    return (int)_queue->size;
                }
            }
        }

        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    BasicTests();
                    return (int)_queue->capacity;
                }
            }
        }

        public static NativeBag Create(AllocatorManager.AllocatorHandle allocator)
        {
            unsafe
            {
                var bag = new NativeBag();
                bag._allocator = allocator;
                var sizeOf = Unsafe.SizeOf<UnsafeBlob>();
                var listData = (UnsafeBlob*)UnsafeUtility.Malloc(sizeOf, 16, allocator.ToAllocator);
                UnsafeUtility.MemClear(listData, sizeOf);
                listData->_allocator = allocator;

                bag._queue = listData;
                return bag;
            }
        }

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    BasicTests();

                    if (_queue == null || _queue->ptr == null)
                        return true;
                }

                return Length == 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Dispose()
        {
            if (_queue != null)
            {
                BasicTests();

                _queue->Dispose();
                UnsafeUtility.Free(_queue, _allocator.ToAllocator);
                _queue = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T ReserveEnqueue<T>(out UnsafeArrayIndex index)
            where T : unmanaged
        {
            unsafe
            {
                BasicTests();

                var sizeOf = Unsafe.SizeOf<T>();

                if (_queue->availableSpace - sizeOf < 0)
                {
                    _queue->Grow<T>();
                }

                return ref _queue->Reserve<T>(out index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue<T>(in T item)
            where T : unmanaged
        {
            unsafe
            {
                BasicTests();

                var sizeOf = Unsafe.SizeOf<T>();
                if (_queue->availableSpace - sizeOf < 0)
                {
                    _queue->Grow<T>();
                }

                _queue->Enqueue(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            unsafe
            {
                BasicTests();
                _queue->Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Dequeue<T>()
            where T : unmanaged
        {
            unsafe
            {
                BasicTests();

                return _queue->Dequeue<T>();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T AccessReserved<T>(UnsafeArrayIndex reservedIndex)
            where T : unmanaged
        {
            unsafe
            {
                BasicTests();
                return ref _queue->AccessReserved<T>(reservedIndex);
            }
        }

        [Conditional("ENABLE_DEBUG_CHECKS")]
        unsafe void BasicTests()
        {
            if (_queue == null)
                throw new TrecsException("SimpleNativeArray: null-access");
        }

        [NoAlias]
        [NativeDisableUnsafePtrRestriction]
        unsafe UnsafeBlob* _queue;

        AllocatorManager.AllocatorHandle _allocator;
    }
}
