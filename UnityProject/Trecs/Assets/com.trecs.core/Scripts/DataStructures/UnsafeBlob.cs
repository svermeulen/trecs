using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    // Opaque index wrapper so callers can't pass arbitrary ints
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct UnsafeArrayIndex
    {
        internal uint Index;
    }

    /// <summary>
    /// Burst-compatible typeless ring-buffer queue. Read and write heads advance
    /// independently; the write head wraps when space freed by dequeues allows it.
    /// The write head cannot surpass the read head.
    /// </summary>
    struct UnsafeBlob : IDisposable
    {
        const int Alignment = 4;
        const int PointerAlignment = 16;

        internal unsafe byte* ptr { get; set; }

        // Set by NativeBag.Create after MemClear, before any Grow / Dispose call.
        internal AllocatorManager.AllocatorHandle _allocator;

        // expressed in bytes
        internal uint capacity { get; private set; }

        // expressed in bytes
        internal uint size
        {
            get
            {
                var currentSize = (uint)_writeIndex - _readIndex;
#if TRECS_INTERNAL_CHECKS && DEBUG
                if ((currentSize & (Alignment - 1)) != 0)
                    throw new TrecsException("size is expected to be a multiple of 4");
#endif

                return currentSize;
            }
        }

        // expressed in bytes
        internal uint availableSpace => capacity - size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Pad4(uint input) => (uint)(-(int)input & (Alignment - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Enqueue<T>(in T item)
            where T : unmanaged
        {
            unsafe
            {
                var structSize = (uint)Unsafe.SizeOf<T>();
                var writeHead = _writeIndex % capacity;

#if TRECS_INTERNAL_CHECKS && DEBUG
                var size = _writeIndex - _readIndex;
                var spaceAvailable = capacity - size;
                if (spaceAvailable - (int)structSize < 0)
                    throw new TrecsException("no writing authorized");

                if ((writeHead & (Alignment - 1)) != 0)
                    throw new TrecsException("write head is expected to be a multiple of 4");
#endif
                if (writeHead + structSize <= capacity)
                {
                    Unsafe.Write(ptr + writeHead, item);
                }
                else
                {
                    // Item wraps around the ring — copy in two parts
                    var byteCountToEnd = capacity - writeHead;

                    var localCopyToAvoidGcIssues = item;
                    Unsafe.CopyBlock(
                        ptr + writeHead,
                        Unsafe.AsPointer(ref localCopyToAvoidGcIssues),
                        (uint)byteCountToEnd
                    );

                    var restCount = structSize - byteCountToEnd;

                    Unsafe.CopyBlock(
                        ptr,
                        (byte*)Unsafe.AsPointer(ref localCopyToAvoidGcIssues) + byteCountToEnd,
                        (uint)restCount
                    );
                }

                // Padding is necessary for mixed-type blobs; use WriteUnaligned to bypass
                uint paddedStructSize = (uint)(structSize + (int)Pad4(structSize));

                _writeIndex += paddedStructSize;
            }
        }

        // Returns an unwrapped index that must be wrapped (% capacity) before use
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref T Reserve<T>(out UnsafeArrayIndex index)
            where T : unmanaged
        {
            unsafe
            {
                var structSize = (uint)Unsafe.SizeOf<T>();
                var wrappedIndex = _writeIndex % capacity;
#if TRECS_INTERNAL_CHECKS && DEBUG
                var size = _writeIndex - _readIndex;
                var spaceAvailable = capacity - size;
                if (spaceAvailable - (int)structSize < 0)
                    throw new TrecsException("no writing authorized");

                if ((wrappedIndex & (Alignment - 1)) != 0)
                    throw new TrecsException("write head is expected to be a multiple of 4");
#endif
                ref var buffer = ref Unsafe.AsRef<T>(ptr + wrappedIndex);

                index.Index = _writeIndex;

                _writeIndex += structSize + Pad4(structSize);

                return ref buffer;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref T AccessReserved<T>(UnsafeArrayIndex index)
            where T : unmanaged
        {
            unsafe
            {
                var wrappedIndex = index.Index % capacity;
#if TRECS_INTERNAL_CHECKS && DEBUG
                TrecsAssert.That((index.Index & (Alignment - 1)) == 0, "invalid index detected");
#endif
                return ref Unsafe.AsRef<T>(ptr + wrappedIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Dequeue<T>()
            where T : unmanaged
        {
            unsafe
            {
                var structSize = (uint)Unsafe.SizeOf<T>();
                var readHead = _readIndex % capacity;

#if TRECS_INTERNAL_CHECKS && DEBUG
                var size = _writeIndex - _readIndex;
                if (size < structSize)
                    throw new TrecsException("dequeuing empty queue or unexpected type dequeued");
                if (_readIndex > _writeIndex)
                    throw new TrecsException("unexpected read");
                if ((readHead & (Alignment - 1)) != 0)
                    throw new TrecsException("read head is expected to be a multiple of 4");
#endif
                var paddedStructSize = structSize + Pad4(structSize);
                _readIndex += paddedStructSize;

                if (_readIndex == _writeIndex)
                {
                    // Queue fully drained — reset both heads to 0 to reduce wrapping
                    _writeIndex = 0;
                    _readIndex = 0;
                }

                if (readHead + paddedStructSize <= capacity)
                    return Unsafe.Read<T>(ptr + readHead);

                // Item wraps around the ring — reconstruct from two parts
                T item = default;
                var byteCountToEnd = capacity - readHead;
                Unsafe.CopyBlock(Unsafe.AsPointer(ref item), ptr + readHead, byteCountToEnd);

                var restCount = structSize - byteCountToEnd;
                Unsafe.CopyBlock(
                    (byte*)Unsafe.AsPointer(ref item) + byteCountToEnd,
                    ptr,
                    restCount
                );

                return item;
            }
        }

        /// <summary>
        /// Grows the backing buffer. Unwrapped indices of existing elements are
        /// preserved so previously reserved <see cref="UnsafeArrayIndex"/> values stay valid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Grow<T>()
            where T : unmanaged
        {
            unsafe
            {
                var sizeOf = Unsafe.SizeOf<T>();

                var oldCapacity = capacity;

                uint newCapacity = (uint)((oldCapacity + sizeOf) << 1);
                // Round up to Alignment so wrapped heads stay aligned
                newCapacity += Pad4(newCapacity);

                byte* newPointer = (byte*)
                    UnsafeUtility.Malloc(newCapacity, PointerAlignment, _allocator.ToAllocator);
                UnsafeUtility.MemClear(newPointer, newCapacity);

                // Copy existing content into the new buffer
                var currentSize = _writeIndex - _readIndex;
                if (currentSize > 0)
                {
                    var oldReaderHead = _readIndex % oldCapacity;
                    var oldWriterHead = _writeIndex % oldCapacity;

                    // Reader behind writer means no wrap — single contiguous copy
                    if (oldReaderHead < oldWriterHead)
                    {
                        var newReaderHead = _readIndex % newCapacity;

                        Unsafe.CopyBlock(
                            newPointer + newReaderHead,
                            ptr + oldReaderHead,
                            (uint)currentSize
                        );
                    }
                    else
                    {
                        // Writer wrapped — copy tail (reader→end) then head (0→writer)
                        var byteCountToEnd = oldCapacity - oldReaderHead;
                        var newReaderHead = _readIndex % newCapacity;

#if TRECS_INTERNAL_CHECKS && DEBUG
                        if (newReaderHead + byteCountToEnd + oldWriterHead > newCapacity)
                            throw new TrecsException(
                                "something is wrong with my previous assumptions"
                            );
#endif
                        Unsafe.CopyBlock(
                            newPointer + newReaderHead,
                            ptr + oldReaderHead,
                            byteCountToEnd
                        );
                        Unsafe.CopyBlock(
                            newPointer + newReaderHead + byteCountToEnd,
                            ptr + 0,
                            (uint)oldWriterHead
                        ); // from start of old array to old writer head (the writer head wrapped)
                    }
                }

                if (ptr != null)
                    UnsafeUtility.Free(ptr, _allocator.ToAllocator);

                ptr = newPointer;
                capacity = newCapacity;

                // NOTE: _readIndex is intentionally NOT reset — it is an unwrapped index that must remain unchanged across resizes.
                _writeIndex = _readIndex + currentSize;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            unsafe
            {
                if (ptr != null)
                    UnsafeUtility.Free(ptr, _allocator.ToAllocator);

                ptr = null;
                _writeIndex = 0;
                capacity = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _writeIndex = 0;
            _readIndex = 0;
        }

        uint _writeIndex;
        uint _readIndex;
    }
}
