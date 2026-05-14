using System;
using System.ComponentModel;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct SharedNativeInt : IDisposable
    {
        [NativeDisableUnsafePtrRestriction]
        unsafe int* data;

        AllocatorManager.AllocatorHandle _allocator;

        public static SharedNativeInt Create(int t, AllocatorManager.AllocatorHandle allocator)
        {
            unsafe
            {
                var current = new SharedNativeInt();
                current._allocator = allocator;
                current.data = (int*)
                    UnsafeUtility.Malloc(
                        sizeof(int),
                        UnsafeUtility.AlignOf<int>(),
                        allocator.ToAllocator
                    );
                *current.data = t;

                return current;
            }
        }

        public static implicit operator int(SharedNativeInt t)
        {
            unsafe
            {
                TrecsRequire.That(t.data != null, "using disposed SharedNativeInt");
                return Volatile.Read(ref *t.data);
            }
        }

        public void Dispose()
        {
            unsafe
            {
                if (data != null)
                {
                    UnsafeUtility.Free(data, _allocator.ToAllocator);
                    data = null;
                }
            }
        }

        public int Decrement()
        {
            unsafe
            {
                TrecsRequire.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.Decrement(ref *data);
            }
        }

        public int Increment()
        {
            unsafe
            {
                TrecsRequire.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.Increment(ref *data);
            }
        }

        public int Add(int val)
        {
            unsafe
            {
                TrecsRequire.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.Add(ref *data, val);
            }
        }

        public int CompareExchange(int value, int compare)
        {
            unsafe
            {
                TrecsRequire.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.CompareExchange(ref *data, value, compare);
            }
        }

        public void Set(int val)
        {
            unsafe
            {
                TrecsRequire.That(data != null, "using disposed SharedNativeInt");
                Volatile.Write(ref *data, val);
            }
        }
    }
}
