using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Fixed size collection
    // Useful for ECS systems where we want everything inside a struct
    public struct FixedArray64<T>
        where T : unmanaged
    {
        const int _length = 64;

#pragma warning disable CS0169
        FixedArray32<T> thirtytwosA;
        FixedArray32<T> thirtytwosB;
#pragma warning restore CS0169

        public readonly int Length => _length;

        public readonly ref readonly T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                TrecsRequire.That(index >= 0 && index < _length, "out of bound index");
                unsafe
                {
                    return ref *((T*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)) + index);
                }
            }
        }

        public override readonly bool Equals(object obj)
        {
            return obj is FixedArray64<T> other && this == other;
        }

        public override readonly int GetHashCode()
        {
            return UnmanagedUtil.BlittableHashCode(this);
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

    public static class FixedArray64Extensions
    {
        /// <summary>
        /// Returns a mutable ref to element <paramref name="index"/>. Requires a
        /// mutable reference to the array, so cannot be called on `in` parameters.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Mut<T>(this ref FixedArray64<T> arr, int index)
            where T : unmanaged
        {
            TrecsRequire.That(index >= 0 && index < 64, "out of bound index");
            unsafe
            {
                return ref *((T*)Unsafe.AsPointer(ref arr) + index);
            }
        }
    }
}
