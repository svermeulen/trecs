using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Fixed size collection
    // Useful for ECS systems where we want everything inside a struct
    public struct FixedArray256<T>
        where T : unmanaged
    {
        static readonly int _length = 256;

#pragma warning disable CS0169
        FixedArray128<T> oneTwentyEightA;
        FixedArray128<T> oneTwentyEightB;
#pragma warning restore CS0169

        public readonly int Length => _length;

        public T this[int index]
        {
            readonly get
            {
                Require.That(index < _length, "out of bound index");
                // need Unsafe.AsRef for readonly access
                return Unsafe.Add(
                    ref Unsafe.As<FixedArray256<T>, T>(ref Unsafe.AsRef(in this)),
                    index
                );
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                Require.That(index < _length, "out of bound index");

                Unsafe.Add(ref Unsafe.As<FixedArray256<T>, T>(ref this), index) = value;
            }
        }

        public override readonly bool Equals(object obj)
        {
            FixedTypeCommon.Log.Warning("Used object Equals on FixedArray256, causing boxing");
            return obj is FixedArray256<T> other && this == other;
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }

        public static bool operator ==(in FixedArray256<T> left, in FixedArray256<T> right)
        {
            return UnmanagedUtil.BlittableEquals(left, right);
        }

        public static bool operator !=(in FixedArray256<T> left, in FixedArray256<T> right)
        {
            return !UnmanagedUtil.BlittableEquals(left, right);
        }
    }

    public static class FixedTypedArray256Extensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetRef<T>(this ref FixedArray256<T> array, int index)
            where T : unmanaged
        {
            Require.That(index < array.Length, "out of bound index");
            return ref Unsafe.Add(ref Unsafe.As<FixedArray256<T>, T>(ref array), index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly T Get<T>(this in FixedArray256<T> array, int index)
            where T : unmanaged
        {
            Require.That(index < array.Length, "out of bound index");
            return ref Unsafe.Add(
                ref Unsafe.As<FixedArray256<T>, T>(ref Unsafe.AsRef(in array)),
                index
            );
        }
    }
}
