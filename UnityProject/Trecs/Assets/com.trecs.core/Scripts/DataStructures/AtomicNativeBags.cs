using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public unsafe struct AtomicNativeBags : IDisposable
    {
        int _threadsCount;

        [NoAlias]
        [NativeDisableUnsafePtrRestriction]
        NativeBag* _data;

        AllocatorManager.AllocatorHandle _allocator;

        public int ThreadSlotCount => _threadsCount;

        public static AtomicNativeBags Create(AllocatorManager.AllocatorHandle allocator)
        {
            var result = new AtomicNativeBags();
            result._allocator = allocator;
            result._threadsCount = JobsUtility.MaxJobThreadCount + 1;

            var bufferSize = Unsafe.SizeOf<NativeBag>();
            var bufferCount = result._threadsCount;
            var allocationSize = bufferSize * bufferCount;

            var ptr = (byte*)UnsafeUtility.Malloc(allocationSize, 16, allocator.ToAllocator);
            UnsafeUtility.MemClear(ptr, allocationSize);

            for (int i = 0; i < bufferCount; i++)
            {
                var bufferPtr = (NativeBag*)(ptr + bufferSize * i);
                var buffer = NativeBag.Create(allocator);
                Unsafe.Write(bufferPtr, buffer);
            }

            result._data = (NativeBag*)ptr;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref NativeBag GetBag(int index)
        {
#if DEBUG
            if (_data == null)
                throw new TrecsException("using invalid AtomicNativeBags");
#endif

            return ref Unsafe.AsRef<NativeBag>(Unsafe.Add<NativeBag>(_data, index));
        }

        public void Dispose()
        {
#if DEBUG
            if (_data == null)
                throw new TrecsException("using invalid AtomicNativeBags");
#endif

            for (int i = 0; i < _threadsCount; i++)
            {
                GetBag(i).Dispose();
            }
            UnsafeUtility.Free(_data, _allocator.ToAllocator);
            _data = null;
        }

        public void Clear()
        {
#if DEBUG
            if (_data == null)
                throw new TrecsException("using invalid AtomicNativeBags");
#endif

            for (int i = 0; i < _threadsCount; i++)
            {
                GetBag(i).Clear();
            }
        }
    }
}
