using System;
using System.ComponentModel;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct SharedNativeInt : IDisposable
    {
        [NativeDisableUnsafePtrRestriction]
        unsafe int* data;

        public static SharedNativeInt Create(int t)
        {
            unsafe
            {
                var current = new SharedNativeInt();
                current.data = (int*)
                    UnsafeUtility.Malloc(
                        sizeof(int),
                        UnsafeUtility.AlignOf<int>(),
                        Allocator.Persistent
                    );
                *current.data = t;

                return current;
            }
        }

        public static implicit operator int(SharedNativeInt t)
        {
            unsafe
            {
                Require.That(t.data != null, "using disposed SharedNativeInt");
                return *t.data;
            }
        }

        public void Dispose()
        {
            unsafe
            {
                if (data != null)
                {
                    UnsafeUtility.Free(data, Allocator.Persistent);
                    data = null;
                }
            }
        }

        public int Decrement()
        {
            unsafe
            {
                Require.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.Decrement(ref *data);
            }
        }

        public int Increment()
        {
            unsafe
            {
                Require.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.Increment(ref *data);
            }
        }

        public int Add(int val)
        {
            unsafe
            {
                Require.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.Add(ref *data, val);
            }
        }

        public int CompareExchange(int value, int compare)
        {
            unsafe
            {
                Require.That(data != null, "using disposed SharedNativeInt");
                return Interlocked.CompareExchange(ref *data, value, compare);
            }
        }

        public void Set(int val)
        {
            unsafe
            {
                Require.That(data != null, "using disposed SharedNativeInt");
                Volatile.Write(ref *data, val);
            }
        }
    }
}
