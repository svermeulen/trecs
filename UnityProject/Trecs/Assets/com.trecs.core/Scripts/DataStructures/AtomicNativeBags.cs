using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public unsafe struct AtomicNativeBags : IDisposable
    {
        uint _threadsCount;

        [Unity.Burst.NoAlias]
        [Unity.Collections.LowLevel.Unsafe.NativeDisableUnsafePtrRestriction]
        NativeBag* _data;

        public uint count => _threadsCount;

        public static AtomicNativeBags Create()
        {
            var result = new AtomicNativeBags();
            result._threadsCount = JobsUtility.MaxJobThreadCount + 1;

            var bufferSize = Unsafe.SizeOf<NativeBag>();
            var bufferCount = result._threadsCount;
            var allocationSize = bufferSize * bufferCount;

            var ptr = (byte*)UnsafeUtility.Malloc(allocationSize, 16, Allocator.Persistent);
            UnsafeUtility.MemClear(ptr, allocationSize);

            for (int i = 0; i < bufferCount; i++)
            {
                var bufferPtr = (NativeBag*)(ptr + bufferSize * i);
                var buffer = NativeBag.Create();
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
            UnsafeUtility.Free(_data, Allocator.Persistent);
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
