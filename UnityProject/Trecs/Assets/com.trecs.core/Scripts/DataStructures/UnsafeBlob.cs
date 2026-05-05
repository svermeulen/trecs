using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
#if TRECS_INTERNAL_CHECKS && DEBUG
#endif

namespace Trecs.Internal
{
    //Necessary to be sure that the user won't pass random values
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct UnsafeArrayIndex
    {
        internal uint Index;
    }

    /// <summary>
    ///     Note: this must work inside burst, so it must follow burst restrictions
    ///     It's a typeless native queue based on a ring-buffer model. This means that the writing head and the
    ///     reading head always advance independently. If there is enough space left by dequeued elements,
    ///     the writing head will wrap around. The writing head cannot ever surpass the reading head.
    ///
    /// </summary>
    struct UnsafeBlob : IDisposable
    {
        const int Alignment = 4;
        const int PointerAlignment = 16;

        internal unsafe byte* ptr { get; set; }

        //expressed in bytes
        internal uint capacity { get; private set; }

        //expressed in bytes
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

        //expressed in bytes
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
                else //copy with wrap, will start to copy and wrap for the remainder
                {
                    var byteCountToEnd = capacity - writeHead;

                    var localCopyToAvoidGcIssues = item;
                    //read and copy the first portion of Item until the end of the stream
                    Unsafe.CopyBlock(
                        ptr + writeHead,
                        Unsafe.AsPointer(ref localCopyToAvoidGcIssues),
                        (uint)byteCountToEnd
                    );

                    var restCount = structSize - byteCountToEnd;

                    //read and copy the remainder
                    Unsafe.CopyBlock(
                        ptr,
                        (byte*)Unsafe.AsPointer(ref localCopyToAvoidGcIssues) + byteCountToEnd,
                        (uint)restCount
                    );
                }

                //this is may seems a waste if you are going to use an unsafeBlob just for bytes, but it's necessary for mixed types.
                //it's still possible to use WriteUnaligned though
                uint paddedStructSize = (uint)(structSize + (int)Pad4(structSize));

                _writeIndex += paddedStructSize; //we want _writeIndex to be always aligned by 4
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //The index returned is the index of the unwrapped ring. It must be wrapped again before to be used
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
                if ((index.Index & (Alignment - 1)) != 0)
                    throw new TrecsException($"invalid index detected");
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
                if (size < structSize) //are there enough bytes to read?
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
                    //resetting the Indices has the benefit to let the Reserve work in more occasions and
                    //the rapping happening less often. If the _readIndex reached the _writeIndex, it means
                    //that there is no data left to read, so we can start to write again from the begin of the memory
                    _writeIndex = 0;
                    _readIndex = 0;
                }

                if (readHead + paddedStructSize <= capacity)
                    return Unsafe.Read<T>(ptr + readHead);

                //handle the case the structure wraps around so it must be reconstructed from the part at the
                //end of the stream and the part starting from the begin.
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
        /// This code unwraps the queue and resizes the array, but doesn't change the unwrapped index of existing elements.
        /// In this way the previously reserved indices will remain valid
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
                //be sure it's multiple of 4. Assuming that what we write is aligned to 4, then we will always have aligned wrapped heads
                //the reading and writing head always increment in multiple of 4
                newCapacity += Pad4(newCapacity);

                byte* newPointer = (byte*)
                    UnsafeUtility.Malloc(newCapacity, PointerAlignment, Allocator.Persistent);
                UnsafeUtility.MemClear(newPointer, newCapacity);

                //copy wrapped content if there is any
                var currentSize = _writeIndex - _readIndex;
                if (currentSize > 0)
                {
                    var oldReaderHead = _readIndex % oldCapacity;
                    var oldWriterHead = _writeIndex % oldCapacity;

                    //Remembering that the unwrapped reader cannot ever surpass the unwrapped writer, if the reader is behind the writer
                    //it means that the writer didn't wrap. It's the natural position so the data can be copied with
                    //a single memcpy
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
                        //if the wrapped writer is behind the wrapped reader, it means the writer wrapped. Therefore
                        //I need to copy the data from the current wrapped reader to the end and then from the
                        //begin of the array to the current wrapped writer.

                        var byteCountToEnd = oldCapacity - oldReaderHead; //bytes to copy from the reader to the end
                        var newReaderHead = _readIndex % newCapacity;

#if TRECS_INTERNAL_CHECKS && DEBUG
                        if (newReaderHead + byteCountToEnd + oldWriterHead > newCapacity) //basically the test is the old size must be less than the new capacity.
                            throw new TrecsException(
                                "something is wrong with my previous assumptions"
                            );
#endif
                        //I am leaving on purpose gap at the begin of the new array if there is any, it will be
                        //anyway used once it's time to wrap.
                        Unsafe.CopyBlock(
                            newPointer + newReaderHead,
                            ptr + oldReaderHead,
                            byteCountToEnd
                        ); //from the old reader head to the end of the old array
                        Unsafe.CopyBlock(
                            newPointer + newReaderHead + byteCountToEnd,
                            ptr + 0,
                            (uint)oldWriterHead
                        ); //from the begin of the old array to the old writer head (rember the writerHead wrapped)
                    }
                }

                if (ptr != null)
                    UnsafeUtility.Free(ptr, Allocator.Persistent);

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
                    UnsafeUtility.Free(ptr, Allocator.Persistent);

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
