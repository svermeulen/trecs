using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Fixed size collection
    // Useful for ECS systems where we want everything inside a struct
    public struct FixedArray64<T>
        where T : unmanaged
    {
        static readonly int _length = 64;

#pragma warning disable CS0169
        FixedArray32<T> thirtytwosA;
        FixedArray32<T> thirtytwosB;
#pragma warning restore CS0169

        public readonly int Length => _length;

        public T this[int index]
        {
            readonly get
            {
                Require.That(index < _length, "out of bound index");
                // need Unsafe.AsRef for readonly access
                return Unsafe.Add(
                    ref Unsafe.As<FixedArray64<T>, T>(ref Unsafe.AsRef(in this)),
                    index
                );
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                Require.That(index < _length, "out of bound index");

                Unsafe.Add(ref Unsafe.As<FixedArray64<T>, T>(ref this), index) = value;
            }
        }

        public override readonly bool Equals(object obj)
        {
            FixedTypeCommon.Log.Warning("Used object Equals on FixedArray64, causing boxing");
            return obj is FixedArray64<T> other && this == other;
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }

        public static bool operator ==(in FixedArray64<T> left, in FixedArray64<T> right)
        {
            return UnmanagedUtil.BlittableEquals(left, right);
        }

        public static bool operator !=(in FixedArray64<T> left, in FixedArray64<T> right)
        {
            return !UnmanagedUtil.BlittableEquals(left, right);
        }
    }

    public static class FixedTypedArray64Extensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetRef<T>(this ref FixedArray64<T> array, int index)
            where T : unmanaged
        {
            Require.That(index < array.Length, "out of bound index");
            return ref Unsafe.Add(ref Unsafe.As<FixedArray64<T>, T>(ref array), index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly T Get<T>(this in FixedArray64<T> array, int index)
            where T : unmanaged
        {
            Require.That(index < array.Length, "out of bound index");
            return ref Unsafe.Add(
                ref Unsafe.As<FixedArray64<T>, T>(ref Unsafe.AsRef(in array)),
                index
            );
        }
    }
}
