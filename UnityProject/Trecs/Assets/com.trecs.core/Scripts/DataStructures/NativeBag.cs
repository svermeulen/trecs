#if TRECS_INTERNAL_CHECKS && DEBUG
#define ENABLE_DEBUG_CHECKS
#endif

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    ///     Burst friendly RingBuffer on steroid:
    ///     it can: Enqueue/Dequeue, it wraps around if there is enough space after dequeuing
    ///     It resizes if there isn't enough space left.
    ///     It's a "bag", you can queue and dequeue any type and mix them. Just be sure that you dequeue what you queue! No check on type
    ///     is done.
    ///     You can reserve a position in the queue to update it later.
    ///     The datastructure is a struct and it's "copiable"
    ///     I eventually decided to call it NativeBag and not NativeBag because it can also be used as
    ///     a preallocated memory pool where any kind of T can be stored as long as T is unmanaged
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NativeBag : IDisposable
    {
        public uint count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    BasicTests();
                    return _queue->size;
                }
            }
        }

        public uint capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    BasicTests();
                    return _queue->capacity;
                }
            }
        }

        public static NativeBag Create()
        {
            unsafe
            {
                var bag = new NativeBag();
                var sizeOf = Unsafe.SizeOf<UnsafeBlob>();
                var listData = (UnsafeBlob*)UnsafeUtility.Malloc(sizeOf, 16, Allocator.Persistent);
                UnsafeUtility.MemClear(listData, sizeOf);

                bag._queue = listData;
                return bag;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEmpty()
        {
            unsafe
            {
                BasicTests();

                if (_queue == null || _queue->ptr == null)
                    return true;
            }

            return count == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Dispose()
        {
            if (_queue != null)
            {
                BasicTests();

                _queue->Dispose();
                UnsafeUtility.Free(_queue, Allocator.Persistent);
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

        [Unity.Burst.NoAlias]
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction]
        unsafe UnsafeBlob* _queue;
    }
}
