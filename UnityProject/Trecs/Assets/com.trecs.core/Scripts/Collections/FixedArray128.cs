using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Fixed size collection
    // Useful for ECS systems where we want everything inside a struct
    public struct FixedArray128<T>
        where T : unmanaged
    {
        const int _length = 128;

#pragma warning disable CS0169
        FixedArray64<T> sixtyfourA;
        FixedArray64<T> sixtyfourB;
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
            return obj is FixedArray128<T> other && this == other;
        }

        public override readonly int GetHashCode()
        {
            return UnmanagedUtil.BlittableHashCode(this);
        }

        public static bool operator ==(in FixedArray128<T> left, in FixedArray128<T> right)
        {
            return UnmanagedUtil.BlittableEquals(left, right);
        }

        public static bool operator !=(in FixedArray128<T> left, in FixedArray128<T> right)
        {
            return !UnmanagedUtil.BlittableEquals(left, right);
        }
    }

    public static class FixedArray128Extensions
    {
        /// <summary>
        /// Returns a mutable ref to element <paramref name="index"/>. Requires a
        /// mutable reference to the array, so cannot be called on `in` parameters.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Mut<T>(this ref FixedArray128<T> arr, int index)
            where T : unmanaged
        {
            TrecsRequire.That(index >= 0 && index < 128, "out of bound index");
            unsafe
            {
                return ref *((T*)Unsafe.AsPointer(ref arr) + index);
            }
        }
    }
}
